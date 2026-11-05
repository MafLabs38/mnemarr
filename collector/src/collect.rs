//! Reads the conversation files of Claude Code, and sends what is new (ADR 0005, ADR 0024).
//!
//! The collector sends the lines as they are in the file, masked, with the position of the first one. The server keeps the position it has
//! reached and answers with it: a batch sent again changes nothing, and a collector that is told another position than the one it expected
//! resumes from it. A line that is cut (the file is being written) waits for the next pass.

use std::fs::{self, File};
use std::io::{self, BufRead, BufReader, Seek, SeekFrom};
use std::path::{Path, PathBuf};

use serde_json::Value;

use crate::mask::Masker;
use crate::state::{FileState, State};
use crate::upload::{Batch, UploadError, Uploader};

/// The most lines in one batch.
pub const MAX_BATCH_LINES: usize = 500;
/// The most bytes in one batch (the server takes 8 MB of request).
pub const MAX_BATCH_BYTES: usize = 3 * 1024 * 1024;
/// A longer line is not sent (the server refuses it): a line of this size is a tool that printed a file, not what was said.
pub const MAX_LINE_BYTES: usize = 1_500_000;
/// What a line that was too long is replaced by, so that the positions of the lines that follow do not move.
pub const DROPPED_LINE: &str = r#"{"type":"collector-dropped-line"}"#;

/// What a pass did.
#[derive(Debug, Default, PartialEq, Eq)]
pub struct Report {
    pub files: usize,
    pub lines_sent: u64,
    /// Why the pass stopped before it was done, if it did: the server could not be reached, or refused the key.
    pub stopped: Option<String>,
    /// The conversations the server refused (a batch that is not one): the others went on.
    pub refused: Vec<String>,
}

/// Reads the conversations of a machine and sends them.
pub struct Collector<U: Uploader> {
    pub machine: String,
    pub projects: PathBuf,
    pub uploader: U,
    masker: Masker,
}

impl<U: Uploader> Collector<U> {
    pub fn new(machine: String, projects: PathBuf, uploader: U) -> Self {
        Self {
            machine,
            projects,
            uploader,
            masker: Masker::new(),
        }
    }

    /// One pass over every conversation: what is new in each is sent, and `state` says how far each has been sent.
    pub fn pass(&self, state: &mut State) -> Report {
        let mut report = Report::default();
        for path in conversation_files(&self.projects) {
            report.files += 1;
            if let Err(stop) = self.send_file(&path, state, &mut report) {
                report.stopped = Some(stop.to_string());
                return report;
            }
        }
        report
    }

    fn send_file(
        &self,
        path: &Path,
        state: &mut State,
        report: &mut Report,
    ) -> Result<(), UploadError> {
        let key = path.to_string_lossy().into_owned();
        let session = path
            .file_stem()
            .map(|name| name.to_string_lossy().into_owned())
            .unwrap_or_default();
        let fallback = project_name(path);
        let file = state.files.entry(key).or_default();
        // A file that is shorter than what was sent has been replaced: start again, the server skips what it has.
        if fs::metadata(path).is_ok_and(|meta| meta.len() < file.offset) {
            *file = FileState::default();
        }
        // Each pass over a file is bounded, so that a server that keeps telling another position cannot keep the collector here.
        for _ in 0..10_000 {
            let Ok(read) = read_batch(path, file.offset) else {
                return Ok(());
            };
            if read.lines.is_empty() {
                return Ok(());
            }
            if file.application.is_none() {
                file.application = application_of(&read.lines);
            }
            let application = file.application.clone().unwrap_or_else(|| fallback.clone());
            let lines: Vec<String> = read
                .lines
                .iter()
                .map(|line| {
                    if line.len() > MAX_LINE_BYTES {
                        DROPPED_LINE.to_string()
                    } else {
                        self.masker.mask(line)
                    }
                })
                .collect();
            let sent = lines.len() as u64;
            let batch = Batch {
                machine: &self.machine,
                application: &application,
                session: &session,
                from: file.lines,
                lines: &lines,
            };
            match self.uploader.send(&batch) {
                Ok(reply) if reply.next == file.lines + sent => {
                    file.offset += read.bytes;
                    file.lines = reply.next;
                    report.lines_sent += reply.accepted;
                }
                Ok(reply) => {
                    // The server is somewhere else than the collector thought: go to where it is, and send from there.
                    let (offset, lines_before) = offset_after(path, reply.next);
                    file.offset = offset;
                    file.lines = lines_before;
                }
                Err(UploadError::Refused(status, reason)) if status != 401 && status != 403 => {
                    report
                        .refused
                        .push(format!("{} ({status}): {reason}", path.display()));
                    return Ok(());
                }
                Err(other) => return Err(other),
            }
        }
        Ok(())
    }
}

/// What was read of a file: the complete lines, and how many bytes they take.
struct Read {
    lines: Vec<String>,
    bytes: u64,
}

/// The complete lines of a file from a position, a batch at most. A line without its end (it is being written) is left.
fn read_batch(path: &Path, offset: u64) -> io::Result<Read> {
    let mut file = File::open(path)?;
    file.seek(SeekFrom::Start(offset))?;
    let mut reader = BufReader::new(file);
    let mut lines = Vec::new();
    let mut bytes = 0u64;
    let mut size = 0usize;
    let mut buffer = Vec::new();
    while lines.len() < MAX_BATCH_LINES && size < MAX_BATCH_BYTES {
        buffer.clear();
        let read = reader.read_until(b'\n', &mut buffer)?;
        if read == 0 || buffer.last() != Some(&b'\n') {
            break;
        }
        bytes += read as u64;
        size += read;
        let text = String::from_utf8_lossy(&buffer);
        lines.push(text.trim_end_matches(['\n', '\r']).to_string());
    }
    Ok(Read { lines, bytes })
}

/// Where the line after the first `count` lines of a file begins, and how many lines there really were before it (fewer than `count` when
/// the file is shorter).
fn offset_after(path: &Path, count: u64) -> (u64, u64) {
    let Ok(file) = File::open(path) else {
        return (0, 0);
    };
    let mut reader = BufReader::new(file);
    let (mut offset, mut lines) = (0u64, 0u64);
    let mut buffer = Vec::new();
    while lines < count {
        buffer.clear();
        match reader.read_until(b'\n', &mut buffer) {
            Ok(read) if read > 0 && buffer.last() == Some(&b'\n') => {
                offset += read as u64;
                lines += 1;
            }
            _ => break,
        }
    }
    (offset, lines)
}

/// The application of a conversation: the last folder of the directory it was held in, as a line of the file says (`cwd`).
fn application_of(lines: &[String]) -> Option<String> {
    lines
        .iter()
        .filter(|line| line.contains("\"cwd\""))
        .find_map(|line| {
            let value: Value = serde_json::from_str(line).ok()?;
            let cwd = value.get("cwd")?.as_str()?;
            cwd.split(['/', '\\'])
                .rfind(|part| !part.is_empty())
                .map(str::to_string)
        })
}

/// The name of the project folder a conversation is in, as Claude Code writes it (`-workspace-mnemarr`): the last word of it. Only when no
/// line of the conversation says where it was held.
fn project_name(path: &Path) -> String {
    path.parent()
        .and_then(Path::file_name)
        .map(|name| name.to_string_lossy().into_owned())
        .and_then(|name| {
            name.rsplit('-')
                .find(|part| !part.is_empty())
                .map(str::to_string)
        })
        .unwrap_or_else(|| "unknown".to_string())
}

/// The conversations of a machine: the `.jsonl` files directly in each project folder (what a sub-agent said is in a folder of its own and is
/// left out), oldest first.
pub fn conversation_files(projects: &Path) -> Vec<PathBuf> {
    let mut files: Vec<(std::time::SystemTime, PathBuf)> = Vec::new();
    let Ok(folders) = fs::read_dir(projects) else {
        return Vec::new();
    };
    for folder in folders.flatten().map(|entry| entry.path()) {
        let Ok(entries) = fs::read_dir(&folder) else {
            continue;
        };
        for path in entries.flatten().map(|entry| entry.path()) {
            if path
                .extension()
                .is_some_and(|extension| extension == "jsonl")
                && let Ok(meta) = fs::metadata(&path)
                && meta.is_file()
            {
                files.push((meta.modified().unwrap_or(std::time::UNIX_EPOCH), path));
            }
        }
    }
    files.sort();
    files.into_iter().map(|(_, path)| path).collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::upload::Reply;
    use std::cell::RefCell;

    /// A server that keeps what it is sent, and answers like Mnemarr: it takes what starts at its position or before, and says where it is.
    struct Fake {
        taken: RefCell<Vec<String>>,
        calls: RefCell<Vec<(u64, usize)>>,
        down: RefCell<Option<UploadError>>,
    }

    impl Fake {
        fn new() -> Self {
            Self {
                taken: RefCell::new(Vec::new()),
                calls: RefCell::new(Vec::new()),
                down: RefCell::new(None),
            }
        }
    }

    impl Uploader for &Fake {
        fn send(&self, batch: &Batch<'_>) -> Result<Reply, UploadError> {
            if let Some(error) = self.down.borrow_mut().take() {
                return Err(error);
            }
            self.calls
                .borrow_mut()
                .push((batch.from, batch.lines.len()));
            let next = self.taken.borrow().len() as u64;
            if batch.from > next {
                return Ok(Reply { next, accepted: 0 });
            }
            let skip = (next - batch.from) as usize;
            let fresh: Vec<String> = batch.lines.iter().skip(skip).cloned().collect();
            self.taken.borrow_mut().extend(fresh.iter().cloned());
            Ok(Reply {
                next: self.taken.borrow().len() as u64,
                accepted: fresh.len() as u64,
            })
        }
    }

    struct Workspace(PathBuf);

    impl Workspace {
        fn new(name: &str) -> Self {
            let root = std::env::temp_dir().join(format!("mnemarr-{name}-{}", std::process::id()));
            let _ = fs::remove_dir_all(&root);
            fs::create_dir_all(root.join("-workspace-mnemarr")).unwrap();
            Self(root)
        }

        fn file(&self) -> PathBuf {
            self.0.join("-workspace-mnemarr").join("session-1.jsonl")
        }
    }

    impl Drop for Workspace {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }

    fn collector<'a>(workspace: &Workspace, fake: &'a Fake) -> Collector<&'a Fake> {
        Collector::new("pc".to_string(), workspace.0.clone(), fake)
    }

    #[test]
    fn should_send_every_complete_line_and_then_only_what_is_new() {
        let workspace = Workspace::new("new");
        fs::write(workspace.file(), "{\"cwd\":\"/work/Mnemarr\"}\n{\"b\":2}\n").unwrap();
        let fake = Fake::new();
        let collector = collector(&workspace, &fake);
        let mut state = State::default();

        let first = collector.pass(&mut state);
        let nothing = collector.pass(&mut state);
        fs::write(
            workspace.file(),
            "{\"cwd\":\"/work/Mnemarr\"}\n{\"b\":2}\n{\"c\":3}\n",
        )
        .unwrap();
        let more = collector.pass(&mut state);

        assert_eq!((first.files, first.lines_sent), (1, 2));
        assert_eq!(nothing.lines_sent, 0);
        assert_eq!(more.lines_sent, 1);
        assert_eq!(fake.taken.borrow().len(), 3);
        assert_eq!(*fake.calls.borrow(), vec![(0, 2), (2, 1)]);
    }

    #[test]
    fn should_leave_a_line_that_is_still_being_written_for_the_next_pass() {
        let workspace = Workspace::new("cut");
        fs::write(workspace.file(), "{\"a\":1}\n{\"b\":").unwrap();
        let fake = Fake::new();
        let collector = collector(&workspace, &fake);
        let mut state = State::default();

        collector.pass(&mut state);
        assert_eq!(fake.taken.borrow().len(), 1);
        fs::write(workspace.file(), "{\"a\":1}\n{\"b\":2}\n").unwrap();
        collector.pass(&mut state);

        assert_eq!(*fake.taken.borrow(), vec!["{\"a\":1}", "{\"b\":2}"]);
    }

    #[test]
    fn should_lose_nothing_when_the_server_is_away_and_send_it_when_it_is_back() {
        let workspace = Workspace::new("away");
        fs::write(workspace.file(), "{\"a\":1}\n{\"b\":2}\n").unwrap();
        let fake = Fake::new();
        *fake.down.borrow_mut() = Some(UploadError::Later("no route".to_string()));
        let collector = collector(&workspace, &fake);
        let mut state = State::default();

        let away = collector.pass(&mut state);
        let back = collector.pass(&mut state);

        assert!(away.stopped.is_some());
        assert_eq!(away.lines_sent, 0);
        assert_eq!(back.lines_sent, 2);
        assert_eq!(fake.taken.borrow().len(), 2);
    }

    #[test]
    fn should_resume_from_where_the_server_is_when_it_has_more_or_less_than_expected() {
        let workspace = Workspace::new("resume");
        fs::write(workspace.file(), "{\"a\":1}\n{\"b\":2}\n{\"c\":3}\n").unwrap();
        let fake = Fake::new();
        fake.taken.borrow_mut().push("{\"a\":1}".to_string());
        let collector = collector(&workspace, &fake);
        // The collector has lost its memory: it thinks it starts from nothing.
        let mut state = State::default();

        let report = collector.pass(&mut state);

        assert_eq!(report.lines_sent, 2);
        assert_eq!(fake.taken.borrow().len(), 3);
        assert_eq!(state.files.values().next().unwrap().lines, 3);
    }

    #[test]
    fn should_mask_secrets_before_they_leave_and_say_where_a_conversation_was_held() {
        let workspace = Workspace::new("mask");
        let secret = format!("ghp_{}", "a".repeat(36));
        fs::write(
            workspace.file(),
            format!("{{\"cwd\":\"/work/Mnemarr\",\"text\":\"{secret}\"}}\n"),
        )
        .unwrap();
        let fake = Fake::new();
        let collector = collector(&workspace, &fake);
        let mut state = State::default();

        collector.pass(&mut state);

        let taken = fake.taken.borrow();
        assert!(taken[0].contains("[masked:github-token]") && !taken[0].contains(&secret));
        assert_eq!(
            state.files.values().next().unwrap().application.as_deref(),
            Some("Mnemarr")
        );
    }

    #[test]
    fn should_replace_a_line_that_is_too_long_and_keep_the_positions() {
        let workspace = Workspace::new("long");
        let huge = "x".repeat(MAX_LINE_BYTES + 10);
        fs::write(workspace.file(), format!("{huge}\n{{\"b\":2}}\n")).unwrap();
        let fake = Fake::new();
        let collector = collector(&workspace, &fake);
        let mut state = State::default();

        collector.pass(&mut state);

        assert_eq!(*fake.taken.borrow(), vec![DROPPED_LINE, "{\"b\":2}"]);
    }

    #[test]
    fn should_stop_at_once_when_the_key_is_refused_and_go_on_when_only_one_batch_is_refused() {
        let workspace = Workspace::new("refused");
        fs::write(workspace.file(), "{\"a\":1}\n").unwrap();
        let fake = Fake::new();
        *fake.down.borrow_mut() = Some(UploadError::Refused(403, "no".to_string()));
        let mut state = State::default();
        let stopped = collector(&workspace, &fake).pass(&mut state);
        *fake.down.borrow_mut() = Some(UploadError::Refused(400, "bad".to_string()));
        let refused = collector(&workspace, &fake).pass(&mut state);

        assert!(stopped.stopped.is_some());
        assert!(refused.stopped.is_none());
        assert_eq!(refused.refused.len(), 1);
    }

    #[test]
    fn should_leave_out_what_a_sub_agent_said_and_what_is_not_a_conversation() {
        let workspace = Workspace::new("files");
        fs::create_dir_all(
            workspace
                .0
                .join("-workspace-mnemarr")
                .join("session-1")
                .join("subagents"),
        )
        .unwrap();
        fs::write(workspace.file(), "{}\n").unwrap();
        fs::write(
            workspace.0.join("-workspace-mnemarr").join("notes.txt"),
            "x",
        )
        .unwrap();
        fs::write(
            workspace
                .0
                .join("-workspace-mnemarr")
                .join("session-1")
                .join("subagents")
                .join("agent-1.jsonl"),
            "{}\n",
        )
        .unwrap();

        let files = conversation_files(&workspace.0);

        assert_eq!(files, vec![workspace.file()]);
    }

    #[test]
    fn should_name_the_application_from_the_project_folder_when_no_line_says_it() {
        let workspace = Workspace::new("name");
        fs::write(workspace.file(), "{\"a\":1}\n").unwrap();
        let fake = Fake::new();
        let collector = collector(&workspace, &fake);
        let mut state = State::default();

        collector.pass(&mut state);

        assert_eq!(project_name(&workspace.file()), "mnemarr");
    }
}
