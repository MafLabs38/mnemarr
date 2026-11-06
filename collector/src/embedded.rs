//! A program that carries its own setting (ADR 0027 §15): the one a person downloads from Mnemarr already knows where Mnemarr is, what the machine is
//! called and the code that joins it, so that there is nothing to type. The setting is a block of JSON written after the program, with its length and
//! a mark at the very end: `[program][json][length: u32, little endian]["MNMRCFG1"]`. The program does not look at what follows it, so it runs as it was.

use std::fs;
use std::io;
use std::path::Path;

use serde_json::{Value, json};

/// The last eight bytes of a program that carries a setting.
pub const MAGIC: &[u8; 8] = b"MNMRCFG1";
/// A setting is a few lines: a longer block is not one.
const MAX_SETTING_BYTES: usize = 8192;

/// What a downloaded program knows: where to join, as whom, with which code.
#[derive(Debug, PartialEq, Eq, Clone)]
pub struct Embedded {
    pub server: String,
    pub machine: String,
    pub code: String,
    /// Whether the person was told that the address is plain `http://` and accepted it (the page said so).
    pub insecure: bool,
}

/// The setting at the end of a program, if it has one, and the program without it.
pub fn split(bytes: &[u8]) -> (&[u8], Option<Embedded>) {
    let Some(end) = bytes.len().checked_sub(MAGIC.len()) else {
        return (bytes, None);
    };
    if &bytes[end..] != MAGIC {
        return (bytes, None);
    }
    let Some(length_at) = end.checked_sub(4) else {
        return (bytes, None);
    };
    let mut length = [0u8; 4];
    length.copy_from_slice(&bytes[length_at..end]);
    let length = u32::from_le_bytes(length) as usize;
    let Some(start) = length_at.checked_sub(length) else {
        return (bytes, None);
    };
    if length > MAX_SETTING_BYTES {
        return (bytes, None);
    }
    match parse(&bytes[start..length_at]) {
        Some(setting) => (&bytes[..start], Some(setting)),
        None => (bytes, None),
    }
}

fn parse(json: &[u8]) -> Option<Embedded> {
    let value: Value = serde_json::from_slice(json).ok()?;
    let field = |name: &str| {
        value
            .get(name)
            .and_then(Value::as_str)
            .filter(|text| !text.is_empty())
            .map(str::to_string)
    };
    Some(Embedded {
        server: field("server")?,
        machine: field("machine")?,
        code: field("code")?,
        insecure: value
            .get("insecure")
            .and_then(Value::as_bool)
            .unwrap_or(false),
    })
}

/// A program with a setting added after it: what Mnemarr does for a download, written here so that it is tried against what reads it.
pub fn append(program: &[u8], setting: &Embedded) -> Vec<u8> {
    let json = json!({
        "server": setting.server,
        "machine": setting.machine,
        "code": setting.code,
        "insecure": setting.insecure,
    })
    .to_string()
    .into_bytes();
    let mut bytes = program.to_vec();
    bytes.extend_from_slice(&json);
    bytes.extend_from_slice(&(json.len() as u32).to_le_bytes());
    bytes.extend_from_slice(MAGIC);
    bytes
}

/// The setting the program at this path carries, and the program without it. Reads the whole file: a collector is a few megabytes.
pub fn read(path: &Path) -> io::Result<(Vec<u8>, Option<Embedded>)> {
    let bytes = fs::read(path)?;
    let (program, setting) = split(&bytes);
    Ok((program.to_vec(), setting))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn setting() -> Embedded {
        Embedded {
            server: "https://mnemarr.example".to_string(),
            machine: "gaming-pc".to_string(),
            code: "ABCD-EFGH-JKLM".to_string(),
            insecure: false,
        }
    }

    #[test]
    fn should_give_back_the_setting_that_was_added_and_the_program_that_it_was_added_to() {
        let program = b"MZ the program, any bytes \x00\x01\xff".to_vec();

        let carried = append(&program, &setting());
        let (found, read) = split(&carried);

        assert_eq!(found, program.as_slice());
        assert_eq!(read, Some(setting()));
    }

    #[test]
    fn should_find_nothing_in_a_program_that_carries_no_setting_or_a_broken_one() {
        let plain = b"just a program".to_vec();
        assert_eq!(split(&plain), (plain.as_slice(), None));
        assert_eq!(split(b"").1, None);
        assert_eq!(split(MAGIC).1, None);

        // The mark is there, the length says more than there is.
        let mut broken = b"abc".to_vec();
        broken.extend_from_slice(&9999u32.to_le_bytes());
        broken.extend_from_slice(MAGIC);
        assert_eq!(split(&broken).1, None);

        // The block is not what a setting is: not JSON, or missing a field.
        for block in [&b"not json"[..], &br#"{"server":"https://x"}"#[..]] {
            let mut bytes = b"program".to_vec();
            bytes.extend_from_slice(block);
            bytes.extend_from_slice(&(block.len() as u32).to_le_bytes());
            bytes.extend_from_slice(MAGIC);
            assert_eq!(split(&bytes).1, None);
        }
    }

    #[test]
    fn should_keep_whether_plain_http_was_accepted_and_refuse_a_block_that_is_far_too_long() {
        let mut http = setting();
        http.insecure = true;
        assert_eq!(split(&append(b"p", &http)).1, Some(http));

        let mut long = b"p".to_vec();
        long.extend(std::iter::repeat_n(b'x', MAX_SETTING_BYTES + 1));
        long.extend_from_slice(&((MAX_SETTING_BYTES + 1) as u32).to_le_bytes());
        long.extend_from_slice(MAGIC);
        assert_eq!(split(&long).1, None);
    }

    #[test]
    fn should_read_the_bytes_that_mnemarr_writes() {
        // The same bytes as the test of the server (EmbeddedSettingFixture): what one writes is what the other reads.
        let json = br#"{"server":"https://mnemarr.example","machine":"pc","code":"ABCD-EFGH-JKLM","insecure":false}"#;
        let mut bytes = b"PROG\x00\xc3\xbf".to_vec();
        bytes.extend_from_slice(json);
        bytes.extend_from_slice(&(json.len() as u32).to_le_bytes());
        bytes.extend_from_slice(MAGIC);

        let (program, read) = split(&bytes);

        assert_eq!(program, b"PROG\x00\xc3\xbf");
        assert_eq!(
            read,
            Some(Embedded {
                machine: "pc".to_string(),
                ..setting()
            })
        );
    }
}
