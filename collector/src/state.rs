//! What the collector remembers between two runs: for each conversation file, how far it has been sent.
//!
//! This is also its queue: a line that could not be sent is still in the file, past the position kept here, and is sent by a later pass.
//! Nothing is lost when the server is away, and the file is the only copy there is to keep.

use std::collections::BTreeMap;
use std::fs;
use std::io::{self, Write};
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
    /// A fingerprint of the first line of the file when it was first read: a file that was replaced by another of the same size or longer is not the same file.
    pub head: u64,
}

/// The position reached in every file, by path.
#[derive(Debug, Default, PartialEq, Eq)]
pub struct State {
    pub files: BTreeMap<String, FileState>,
    /// Which server and machine these positions are for. Positions are what a server holds, so they are only good for the one that was sent to:
    /// a machine that joins again under another name, or a server that was emptied, is a new beginning.
    pub identity: Option<String>,
    /// Passes left before the positions are compared with what the server holds (0: at the next pass). Not kept: a collector that starts compares at once.
    pub check_in: u32,
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
                        head: entry.get("head").and_then(Value::as_u64).unwrap_or(0),
                    },
                );
            }
        }
        let identity = document
            .get("identity")
            .and_then(Value::as_str)
            .map(str::to_string);
        Self {
            files,
            identity,
            check_in: 0,
        }
    }

    /// Says which server and machine this state is for. When it was for another (or for none, as in a state kept by an older collector), it starts
    /// again from nothing, since what was sent was sent to somebody else: the server skips what it already has. True when it did.
    pub fn adopt(&mut self, identity: &str) -> bool {
        if self.identity.as_deref() == Some(identity) {
            return false;
        }
        self.files.clear();
        self.identity = Some(identity.to_string());
        true
    }

    /// Writes the state beside its place and moves it in, so that a crash leaves the old one.
    pub fn save(&self, path: &Path) -> io::Result<()> {
        let files: serde_json::Map<String, Value> = self
            .files
            .iter()
            .map(|(name, file)| {
                (
                    name.clone(),
                    json!({ "offset": file.offset, "lines": file.lines, "application": file.application, "head": file.head }),
                )
            })
            .collect();
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent)?;
        }
        let temporary = path.with_extension("tmp");
        // Written and flushed to the disk before it takes the place of the old one: a crash leaves the old file or the whole new one.
        let mut file = fs::File::create(&temporary)?;
        file.write_all(&serde_json::to_vec_pretty(
            &json!({ "identity": self.identity, "files": files }),
        )?)?;
        file.sync_all()?;
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
                head: 42,
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

    #[test]
    fn should_start_again_for_another_server_or_machine_and_keep_what_it_has_for_the_same() {
        let mut state = State::default();
        state.files.insert(
            "/a.jsonl".to_string(),
            FileState {
                offset: 10,
                lines: 1,
                application: None,
                head: 1,
            },
        );

        assert!(
            state.adopt("https://one.example|pc"),
            "a state of nobody is for nobody: it starts"
        );
        assert!(state.files.is_empty());
        state.files.insert(
            "/a.jsonl".to_string(),
            FileState {
                offset: 10,
                lines: 1,
                application: None,
                head: 1,
            },
        );
        assert!(!state.adopt("https://one.example|pc"));
        assert_eq!(
            state.files.len(),
            1,
            "the same machine of the same server keeps its positions"
        );
        assert!(
            state.adopt("https://one.example|other-name"),
            "the machine joined again under another name"
        );
        assert!(state.files.is_empty());
        assert!(
            state.adopt("https://two.example|other-name"),
            "another server"
        );
    }

    #[test]
    fn should_remember_whom_it_is_for_between_two_runs() {
        let folder = std::env::temp_dir().join(format!("mnemarr-state-id-{}", std::process::id()));
        let path = folder.join("state.json");
        let mut state = State::default();
        state.adopt("https://one.example|pc");
        state.save(&path).unwrap();

        let mut loaded = State::load(&path);

        assert!(!loaded.adopt("https://one.example|pc"));
        fs::remove_dir_all(folder).unwrap();
    }
}
