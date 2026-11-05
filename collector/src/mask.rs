//! Hides what looks like a secret before a line leaves the machine (D-10).
//!
//! The server masks again with the same rules, and a file of samples (`tests/masking/samples.json`) is run by the tests of both, so that two
//! engines written by hand do not drift (Q-27). Lines are masked as they are written in JSON: the quotes and line breaks are escaped.

use regex::Regex;

/// A rule: what to look for, and what to put in its place.
struct Rule {
    pattern: Regex,
    replacement: &'static str,
}

/// A character that stands for "a boundary" while the rules are applied (see [`Masker::mask`]).
const BOUNDARY: char = '\u{1}';

/// The rules, in the order they are applied.
pub struct Masker {
    rules: Vec<Rule>,
    escapes: Option<Regex>,
}

const RULES: [(&str, &str); 10] = [
    (
        r"(?s)-----BEGIN [A-Z ]*PRIVATE KEY-----.*?-----END [A-Z ]*PRIVATE KEY-----",
        "[masked:private-key]",
    ),
    (r"\bgithub_pat_[A-Za-z0-9_]{40,}", "[masked:github-token]"),
    (r"\bgh[pousr]_[A-Za-z0-9]{30,}", "[masked:github-token]"),
    (r"\bsk-[A-Za-z0-9_-]{20,}", "[masked:api-key]"),
    (r"\b(?:AKIA|ASIA)[A-Z0-9]{16}\b", "[masked:aws-key]"),
    (r"\bxox[abprs]-[A-Za-z0-9-]{10,}", "[masked:slack-token]"),
    (
        r"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}",
        "[masked:jwt]",
    ),
    (
        r"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]{20,}",
        "Bearer [masked:token]",
    ),
    (
        r"(https?://)[^/\s:@]+:[^/\s@]+@",
        "${1}[masked:credentials]@",
    ),
    (
        r#"(?i)\b([A-Z0-9_]*(?:PASSWORD|PASSWD|SECRET|TOKEN|API_?KEY|ACCESS_?KEY|PRIVATE_?KEY)[A-Z0-9_]*)((?:\\?["'])?\s*[=:]\s*(?:\\?["'])?)[^\s"',;\\]{8,}"#,
        "${1}${2}[masked]",
    ),
];

impl Masker {
    /// The rules of this version.
    ///
    /// A rule that does not compile is a bug of this program, found by its tests: it is left out rather than stopping the collector, and the
    /// server masks again.
    pub fn new() -> Self {
        let rules = RULES
            .iter()
            .filter_map(|(pattern, replacement)| {
                Regex::new(pattern).ok().map(|pattern| Rule {
                    pattern,
                    replacement,
                })
            })
            .collect();
        Self {
            rules,
            // A pattern that is a literal of this program: it compiles, or the program is wrong and its tests say so.
            escapes: Regex::new(r"\\[nrtbf]").ok(),
        }
    }

    /// Whether every rule is in force.
    pub fn is_complete(&self) -> bool {
        self.rules.len() == RULES.len() && self.escapes.is_some()
    }

    /// The text with every secret it contains replaced by a word that says what was there.
    ///
    /// In a JSON line a line break is the two characters `\n`, and without a word boundary after it a token that starts the next line would
    /// not be seen (the usual shape of a pasted token): a marker is put after each such escape while the rules are applied, and taken out.
    pub fn mask(&self, text: &str) -> String {
        let plain = text.replace(BOUNDARY, "");
        let mut masked = match &self.escapes {
            Some(escapes) => escapes
                .replace_all(&plain, |captures: &regex::Captures| {
                    format!("{}{BOUNDARY}", &captures[0])
                })
                .into_owned(),
            None => plain,
        };
        for rule in &self.rules {
            masked = rule
                .pattern
                .replace_all(&masked, rule.replacement)
                .into_owned();
        }
        masked.replace(BOUNDARY, "")
    }
}

impl Default for Masker {
    fn default() -> Self {
        Self::new()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use regex::Regex;

    /// A marker stands for a run of characters, so that the file of samples holds nothing a secret scanner would take for a secret.
    fn expand(text: &str) -> String {
        let marker = Regex::new(r"<([AUD]):(\d+)>").unwrap();
        marker
            .replace_all(text, |captures: &regex::Captures| {
                let character = match &captures[1] {
                    "A" => "a",
                    "U" => "A",
                    _ => "-",
                };
                character.repeat(captures[2].parse().unwrap())
            })
            .into_owned()
    }

    #[test]
    fn should_give_the_answer_of_the_shared_samples() {
        let path = concat!(env!("CARGO_MANIFEST_DIR"), "/../tests/masking/samples.json");
        let document: serde_json::Value =
            serde_json::from_str(&std::fs::read_to_string(path).unwrap()).unwrap();
        let samples = document["samples"].as_array().unwrap();
        assert!(samples.len() >= 10);
        let masker = Masker::new();
        for sample in samples {
            let name = sample["name"].as_str().unwrap();
            let input = expand(sample["input"].as_str().unwrap());
            let expected = expand(sample["expected"].as_str().unwrap());
            assert_eq!(masker.mask(&input), expected, "sample: {name}");
        }
    }

    #[test]
    fn should_keep_a_json_line_valid_when_it_hides_a_secret_in_a_string() {
        let secret = format!("ghp_{}", "a".repeat(36));
        let line = format!(
            r#"{{"type":"user","message":{{"content":"export PASSWORD=hunter2hunter2 and {secret}"}}}}"#
        );

        let masked = Masker::new().mask(&line);

        let value: serde_json::Value = serde_json::from_str(&masked).unwrap();
        assert_eq!(
            value["message"]["content"],
            "export PASSWORD=[masked] and [masked:github-token]"
        );
    }

    #[test]
    fn should_leave_a_secret_written_with_a_json_escape_to_the_second_pass_of_the_server() {
        // The contract: the collector looks at the text of the line, and the server looks again at the decoded text (ADR 0024), where it finds this one.
        let line = format!(r#"{{"text":"\u0067hp_{}"}}"#, "a".repeat(36));

        assert_eq!(Masker::new().mask(&line), line);
    }
}
