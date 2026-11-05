//! What the collector remembers between two runs: for each conversation file, how far it has been sent.
//!
//! This is also its queue: a line that could not be sent is still in the file, past the position kept here, and is sent by a later pass.
//! Nothing is lost when the server is away, and the file is the only copy there is to keep.

use std::collections::BTreeMap;
use std::fs;
use std::io;
use std::path::Path;

use serde_json::{Value, json};

/// How far one conversation file has been sent.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct FileState {
    /// The bytes of the file that were sent: always the end of a line.
    pub offset: u64,
    /// The lines of the file that were sent: the position the server resumes from.
    pub lines: u64,
    /// The application of the conversation, once a line has said where it was held.
    pub application: Option<String>,
}

/// The position reached in every file, by path.
#[derive(Debug, Default, PartialEq, Eq)]
pub struct State {
    pub files: BTreeMap<String, FileState>,
}

impl State {
    /// The state kept in a file. A file that is not there, or that cannot be read, is a first run: the server skips what it already has.
    pub fn load(path: &Path) -> Self {
        let Ok(text) = fs::read_to_string(path) else {
            return Self::default();
        };
        let Ok(document) = serde_json::from_str::<Value>(&text) else {
            return Self::default();
        };
        let mut files = BTreeMap::new();
        if let Some(entries) = document.get("files").and_then(Value::as_object) {
            for (name, entry) in entries {
                files.insert(
                    name.clone(),
                    FileState {
                        offset: entry.get("offset").and_then(Value::as_u64).unwrap_or(0),
                        lines: entry.get("lines").and_then(Value::as_u64).unwrap_or(0),
                        application: entry
                            .get("application")
                            .and_then(Value::as_str)
                            .map(str::to_string),
                    },
                );
            }
        }
        Self { files }
    }

    /// Writes the state beside its place and moves it in, so that a crash leaves the old one.
    pub fn save(&self, path: &Path) -> io::Result<()> {
        let files: serde_json::Map<String, Value> = self
            .files
            .iter()
            .map(|(name, file)| {
                (
                    name.clone(),
                    json!({ "offset": file.offset, "lines": file.lines, "application": file.application }),
                )
            })
            .collect();
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent)?;
        }
        let temporary = path.with_extension("tmp");
        fs::write(
            &temporary,
            serde_json::to_vec_pretty(&json!({ "files": files }))?,
        )?;
        fs::rename(&temporary, path)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn should_give_back_what_it_saved() {
        let folder = std::env::temp_dir().join(format!("mnemarr-state-{}", std::process::id()));
        let path = folder.join("state.json");
        let mut state = State::default();
        state.files.insert(
            "/a/b.jsonl".to_string(),
            FileState {
                offset: 120,
                lines: 3,
                application: Some("mnemarr".to_string()),
            },
        );

        state.save(&path).unwrap();

        assert_eq!(State::load(&path), state);
        fs::remove_dir_all(folder).unwrap();
    }

    #[test]
    fn should_start_again_from_nothing_when_the_file_is_missing_or_damaged() {
        let folder = std::env::temp_dir().join(format!("mnemarr-state-bad-{}", std::process::id()));
        fs::create_dir_all(&folder).unwrap();
        let path = folder.join("state.json");
        assert_eq!(State::load(&path), State::default());

        fs::write(&path, "{ not json").unwrap();

        assert_eq!(State::load(&path), State::default());
        fs::remove_dir_all(folder).unwrap();
    }
}
