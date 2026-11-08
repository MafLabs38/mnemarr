//! The memory without being asked (D-12, ADR 0037): at the start of a session of an assistant, a hook runs `mnemarr-collector context`, which asks the Mnemarr this machine is linked to what
//! it knows of the application (the last folder of the working directory) and prints it, so that the assistant starts with it instead of having to think of asking.
//!
//! A hook must never get in the way: no key, no answer in a few seconds, a Mnemarr that knows nothing, a refusal: nothing is printed and the exit code is 0.

use std::fs;
use std::path::{Path, PathBuf};
use std::time::Duration;

use serde_json::{Value, json};

use crate::config::claude_dir;
use crate::destinations::{self, Destination};

/// How long the hook waits for Mnemarr: a session must not wait for a memory.
const TIMEOUT: Duration = Duration::from_secs(5);

/// The file with the key that reads (the ingest key of a collector reads nothing, D-09), in the folder of the collector: `read-key.<name of the Mnemarr>`, or `read-key`.
const READ_KEY: &str = "read-key";

/// The marker by which the hook of this program is recognised in the settings of an assistant.
const HOOK_MARKER: &str = "mnemarr-collector";

/// How the context is printed.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum Format {
    /// The text as it is.
    #[default]
    Text,
    /// What the hook of Claude Code expects of a start of session: a JSON document with the context to add.
    ClaudeHook,
}

impl Format {
    pub fn parse(name: &str) -> Option<Self> {
        match name {
            "text" => Some(Self::Text),
            "claude-hook" => Some(Self::ClaudeHook),
            _ => None,
        }
    }
}

/// The application of a session: the last folder of the working directory that the hook was given (`{"cwd": "..."}` on its input), when there is one.
pub fn application_of_hook_input(input: &str) -> Option<String> {
    let value: Value = serde_json::from_str(input).ok()?;
    last_folder(value.get("cwd")?.as_str()?)
}

/// The last folder of a path, with either separator.
pub fn last_folder(path: &str) -> Option<String> {
    path.split(['/', '\\'])
        .rfind(|part| !part.is_empty() && !part.ends_with(':'))
        .map(str::to_string)
}

/// The context as the hook prints it.
pub fn render(text: &str, format: Format) -> String {
    match format {
        Format::Text => text.to_string(),
        Format::ClaudeHook => json!({
            "hookSpecificOutput": { "hookEventName": "SessionStart", "additionalContext": text }
        })
        .to_string(),
    }
}

/// The destination to ask and the key to ask it with: the first Mnemarr of the list that has a read key.
pub fn reader_of(dir: &Path) -> Option<(Destination, String)> {
    let listed = destinations::load(dir);
    for destination in &listed {
        for name in [
            format!("{READ_KEY}.{}", destination.name),
            READ_KEY.to_string(),
        ] {
            if let Ok(key) = fs::read_to_string(dir.join(&name)) {
                let key = key.trim().to_string();
                if !key.is_empty() {
                    return Some((destination.clone(), key));
                }
            }
        }
    }
    None
}

/// What Mnemarr says it knows of an application: the text (empty when it knows nothing). Any failure is an error the caller keeps to itself.
pub fn fetch(
    destination: &Destination,
    key: &str,
    application: &str,
    budget: Option<u32>,
) -> Result<String, String> {
    crate::config::check_server(&destination.server, destination.insecure)
        .map_err(|error| error.0)?;
    let agent: ureq::Agent = ureq::Agent::config_builder()
        .timeout_global(Some(TIMEOUT))
        .http_status_as_error(false)
        .max_redirects(0)
        .user_agent(format!("mnemarr-collector/{}", crate::VERSION))
        .build()
        .into();
    let mut url = format!(
        "{}/api/v1/context?application={}",
        destination.server.trim_end_matches('/'),
        encode(application)
    );
    if let Some(budget) = budget {
        url.push_str(&format!("&budget={budget}"));
    }
    let mut response = agent
        .get(&url)
        .header("X-Api-Key", key)
        .call()
        .map_err(|error| error.to_string())?;
    if response.status().as_u16() != 200 {
        return Err(format!("status {}", response.status().as_u16()));
    }
    let body = response
        .body_mut()
        .read_to_string()
        .map_err(|error| error.to_string())?;
    let document: Value = serde_json::from_str(&body).map_err(|error| error.to_string())?;
    Ok(document
        .get("text")
        .and_then(Value::as_str)
        .unwrap_or_default()
        .to_string())
}

fn encode(text: &str) -> String {
    text.bytes()
        .map(|byte| match byte {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'.' | b'_' | b'~' => {
                (byte as char).to_string()
            }
            other => format!("%{other:02X}"),
        })
        .collect()
}

/// What `context-hook` did to the settings of Claude Code.
#[derive(Debug, PartialEq, Eq)]
pub enum HookChange {
    Added,
    Updated,
    AlreadyThere,
    Removed,
    NotThere,
}

/// Where Claude Code keeps its settings: in its configuration folder (`CLAUDE_CONFIG_DIR`, else `.claude` in the home).
pub fn settings_path(
    get: &dyn Fn(&str) -> Option<String>,
    home: Option<&PathBuf>,
) -> Option<PathBuf> {
    claude_dir(get, home).map(|dir| dir.join("settings.json"))
}

fn is_ours(entry: &Value) -> bool {
    entry
        .get("hooks")
        .and_then(Value::as_array)
        .is_some_and(|hooks| {
            hooks.iter().any(|hook| {
                hook.get("command")
                    .and_then(Value::as_str)
                    .is_some_and(|command| {
                        command.contains(HOOK_MARKER) && command.contains(" context")
                    })
            })
        })
}

/// Adds (or updates, or with `remove` takes away) the hook that runs at the start of a session, in a settings document. Pure: the document in, the document out, and what changed. Everything else in
/// the settings is left as it was.
pub fn change_settings(
    settings: &Value,
    command: &str,
    remove: bool,
) -> Result<(Value, HookChange), String> {
    let mut document = settings.clone();
    let Some(root) = document.as_object_mut() else {
        return Err("the settings of Claude Code are not a JSON object".to_string());
    };
    let hooks = root.entry("hooks").or_insert_with(|| json!({}));
    let Some(hooks) = hooks.as_object_mut() else {
        return Err("the hooks of the settings of Claude Code are not an object".to_string());
    };
    let session_start = hooks.entry("SessionStart").or_insert_with(|| json!([]));
    let Some(entries) = session_start.as_array_mut() else {
        return Err("SessionStart in the settings of Claude Code is not a list".to_string());
    };
    let position = entries.iter().position(is_ours);
    let wanted = json!({ "matcher": "startup|resume|clear", "hooks": [{ "type": "command", "command": command, "timeout": 10 }] });
    let change = match (position, remove) {
        (Some(index), true) => {
            entries.remove(index);
            HookChange::Removed
        }
        (None, true) => HookChange::NotThere,
        (Some(index), false) if entries[index] == wanted => HookChange::AlreadyThere,
        (Some(index), false) => {
            entries[index] = wanted;
            HookChange::Updated
        }
        (None, false) => {
            entries.push(wanted);
            HookChange::Added
        }
    };
    if entries.is_empty() {
        hooks.remove("SessionStart");
        if hooks.is_empty() {
            root.remove("hooks");
        }
    }
    Ok((document, change))
}

/// Applies {@link change_settings} to the settings file of Claude Code, keeping a copy of the file as it was (once) beside it.
pub fn change_settings_file(
    path: &Path,
    command: &str,
    remove: bool,
) -> Result<HookChange, String> {
    let current = match fs::read_to_string(path) {
        Ok(text) if text.trim().is_empty() => json!({}),
        Ok(text) => serde_json::from_str::<Value>(&text).map_err(|error| {
            format!("the settings of Claude Code cannot be read ({error}): nothing was changed")
        })?,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => json!({}),
        Err(error) => return Err(error.to_string()),
    };
    let (changed, change) = change_settings(&current, command, remove)?;
    if matches!(change, HookChange::AlreadyThere | HookChange::NotThere) {
        return Ok(change);
    }
    if let Some(folder) = path.parent() {
        fs::create_dir_all(folder).map_err(|error| error.to_string())?;
    }
    let backup = path.with_extension("json.mnemarr-backup");
    if path.exists() && !backup.exists() {
        fs::copy(path, &backup).map_err(|error| error.to_string())?;
    }
    let text = serde_json::to_string_pretty(&changed).map_err(|error| error.to_string())?;
    fs::write(path, format!("{text}\n")).map_err(|error| error.to_string())?;
    Ok(change)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn should_take_the_application_from_the_working_directory_of_the_hook_input() {
        assert_eq!(
            application_of_hook_input(r#"{"cwd":"/workspace/mnemarr","source":"startup"}"#)
                .as_deref(),
            Some("mnemarr")
        );
        assert_eq!(
            application_of_hook_input(r#"{"cwd":"L:\\Workspace\\oter-iptv\\"}"#).as_deref(),
            Some("oter-iptv")
        );
        assert_eq!(application_of_hook_input(r#"{"cwd":"L:\\"}"#), None);
        assert_eq!(application_of_hook_input("not json"), None);
        assert_eq!(application_of_hook_input(r#"{"source":"startup"}"#), None);
    }

    #[test]
    fn should_print_the_context_as_text_or_as_what_the_hook_of_claude_code_expects() {
        assert_eq!(render("hello", Format::Text), "hello");
        let hook: Value =
            serde_json::from_str(&render("hello \"you\"", Format::ClaudeHook)).unwrap();
        assert_eq!(hook["hookSpecificOutput"]["hookEventName"], "SessionStart");
        assert_eq!(
            hook["hookSpecificOutput"]["additionalContext"],
            "hello \"you\""
        );
        assert_eq!(Format::parse("claude-hook"), Some(Format::ClaudeHook));
        assert_eq!(Format::parse("html"), None);
    }

    #[test]
    fn should_escape_the_name_of_an_application_in_the_query() {
        assert_eq!(encode("my app&x"), "my%20app%26x");
        assert_eq!(encode("oter-iptv_2.0"), "oter-iptv_2.0");
    }

    #[test]
    fn should_add_the_hook_once_keep_the_rest_of_the_settings_and_update_a_changed_command() {
        let settings = json!({
            "model": "opus",
            "hooks": { "SessionStart": [{ "matcher": "startup", "hooks": [{ "type": "command", "command": "echo mine" }] }], "Stop": [] }
        });
        let command = "\"/opt/mnemarr-collector\" context --format claude-hook";

        let (added, change) = change_settings(&settings, command, false).unwrap();
        assert_eq!(change, HookChange::Added);
        assert_eq!(added["model"], "opus");
        assert_eq!(added["hooks"]["SessionStart"].as_array().unwrap().len(), 2);
        assert_eq!(
            added["hooks"]["SessionStart"][0]["hooks"][0]["command"],
            "echo mine"
        );
        assert_eq!(added["hooks"]["Stop"], json!([]));

        let (same, again) = change_settings(&added, command, false).unwrap();
        assert_eq!((same == added, again), (true, HookChange::AlreadyThere));
        let (moved, updated) = change_settings(
            &added,
            "\"/new/mnemarr-collector\" context --format claude-hook",
            false,
        )
        .unwrap();
        assert_eq!(updated, HookChange::Updated);
        assert_eq!(moved["hooks"]["SessionStart"].as_array().unwrap().len(), 2);
    }

    #[test]
    fn should_take_the_hook_away_and_leave_the_settings_as_they_were_when_there_was_none() {
        let command = "\"/opt/mnemarr-collector\" context --format claude-hook";
        let (added, _) = change_settings(&json!({}), command, false).unwrap();

        let (removed, change) = change_settings(&added, command, true).unwrap();
        let (untouched, nothing) =
            change_settings(&json!({ "model": "opus" }), command, true).unwrap();

        assert_eq!((removed, change), (json!({}), HookChange::Removed));
        assert_eq!(
            (untouched, nothing),
            (
                json!({ "model": "opus", "hooks": {} })
                    .as_object()
                    .map(|_| json!({ "model": "opus" }))
                    .unwrap(),
                HookChange::NotThere
            )
        );
    }

    #[test]
    fn should_refuse_settings_that_are_not_what_it_expects_instead_of_overwriting_them() {
        assert!(change_settings(&json!([1]), "x", false).is_err());
        assert!(change_settings(&json!({ "hooks": 3 }), "x", false).is_err());
        assert!(change_settings(&json!({ "hooks": { "SessionStart": {} } }), "x", false).is_err());
    }

    #[test]
    fn should_write_the_settings_file_keep_a_copy_once_and_not_touch_a_file_it_cannot_read() {
        let folder = std::env::temp_dir().join(format!("mnemarr-hook-{}", std::process::id()));
        let _ = fs::remove_dir_all(&folder);
        fs::create_dir_all(&folder).unwrap();
        let path = folder.join("settings.json");
        fs::write(&path, "{\"model\":\"opus\"}").unwrap();
        let command = "\"/opt/mnemarr-collector\" context --format claude-hook";

        assert_eq!(
            change_settings_file(&path, command, false).unwrap(),
            HookChange::Added
        );
        assert_eq!(
            fs::read_to_string(folder.join("settings.json.mnemarr-backup")).unwrap(),
            "{\"model\":\"opus\"}"
        );
        assert_eq!(
            change_settings_file(&path, command, false).unwrap(),
            HookChange::AlreadyThere
        );
        assert_eq!(
            change_settings_file(&path, command, true).unwrap(),
            HookChange::Removed
        );
        assert_eq!(
            serde_json::from_str::<Value>(&fs::read_to_string(&path).unwrap()).unwrap(),
            json!({ "model": "opus" })
        );

        fs::write(&path, "{ not json").unwrap();
        assert!(change_settings_file(&path, command, false).is_err());
        assert_eq!(fs::read_to_string(&path).unwrap(), "{ not json");
        let _ = fs::remove_dir_all(&folder);
    }

    #[test]
    fn should_find_the_read_key_of_the_first_mnemarr_that_has_one() {
        let folder = std::env::temp_dir().join(format!("mnemarr-readkey-{}", std::process::id()));
        let _ = fs::remove_dir_all(&folder);
        fs::create_dir_all(&folder).unwrap();
        assert!(reader_of(&folder).is_none());
        fs::write(
            folder.join("destinations.json"),
            r#"{"destinations":[{"name":"a.example","server":"https://a.example","machine":"pc","insecure":false,"keyFile":"key","stateFile":"state.json"}]}"#,
        )
        .unwrap();
        assert!(reader_of(&folder).is_none(), "no read key yet");

        fs::write(folder.join("read-key"), "  the-key\n").unwrap();

        let (destination, key) = reader_of(&folder).unwrap();
        assert_eq!(
            (destination.name.as_str(), key.as_str()),
            ("a.example", "the-key")
        );
        let _ = fs::remove_dir_all(&folder);
    }
}
