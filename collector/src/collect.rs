//! Reads the conversation files of Claude Code, and sends what is new (ADR 0005, ADR 0024).
//!
//! The collector sends the lines as they are in the file, masked, with the position of the first one. The server keeps the position it has
//! reached and answers with it: a batch sent again changes nothing, and a collector that is told another position than the one it expected
//! resumes from it. A line that is cut (the file is being written) waits for the next pass.

use std::fs::{self, File};
use std::io::{self, BufRead, BufReader, Read as _, Seek, SeekFrom};
use std::path::{Path, PathBuf};

use serde_json::Value;

use crate::mask::Masker;
use crate::state::{FileState, State};
use crate::upload::{Batch, UploadError, Uploader};

/// The reader of this collector: the format of the lines it sends.
pub const FORMAT_CLAUDE_CODE: &str = "claude-code";
/// The most batches of one file in one pass: a server that keeps telling another position cannot keep the collector in one file for ever.
const MAX_BATCHES_PER_FILE: usize = 10_000;
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
    /// The files that could not be read (a permission, a file that went away): the others went on.
    pub unreadable: Vec<String>,
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

    /// Whether every rule of the masking is in force: a rule that did not compile is a weaker collector, and its start says so.
    pub fn masking_is_complete(&self) -> bool {
        self.masker.is_complete()
    }

    /// One pass over every conversation: what is new in each is sent, and `state` says how far each has been sent.
    pub fn pass(&self, state: &mut State) -> Report {
        let mut report = Report::default();
        let files = conversation_files(&self.projects);
        if !files.is_empty() {
            // What was said of files that are gone is not kept for ever.
            let present: std::collections::HashSet<String> = files
                .iter()
                .map(|path| path.to_string_lossy().into_owned())
                .collect();
            state.files.retain(|name, _| present.contains(name));
        }
        for path in files {
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
        // A file that is shorter than what was sent, or that does not begin as it did, has been replaced: start again, the server skips what it has.
        let head = fingerprint(path).unwrap_or(0);
        let replaced = fs::metadata(path).is_ok_and(|meta| meta.len() < file.offset)
            || (file.offset > 0 && file.head != 0 && head != 0 && head != file.head);
        if replaced {
            *file = FileState::default();
        }
        if file.head == 0 {
            file.head = head;
        }
        // Each pass over a file is bounded, so that a server that keeps telling another position cannot keep the collector here.
        for _ in 0..MAX_BATCHES_PER_FILE {
            let Ok(read) = read_batch(path, file.offset) else {
                report.unreadable.push(path.display().to_string());
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
                format: FORMAT_CLAUDE_CODE,
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
                    let moved = lines_before != file.lines;
                    file.offset = offset;
                    file.lines = lines_before;
                    if !moved {
                        // No nearer than before: the same batch would be sent again for ever. The next pass tries again.
                        return Ok(());
                    }
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

/// What one line of a file is.
enum Line {
    /// A line with its end: the text, and the bytes it takes.
    Complete(String, u64),
    /// A line that is too long to be a message: it was not kept (the rest of it was skipped without being held), and takes this many bytes.
    TooLong(u64),
}

/// The next line of a file, or `None` when what is left has no end yet (it is being written). A line longer than the limit is never held whole.
fn next_line(reader: &mut BufReader<File>) -> io::Result<Option<Line>> {
    let mut buffer = Vec::new();
    let read = reader
        .by_ref()
        .take(MAX_LINE_BYTES as u64 + 1)
        .read_until(b'\n', &mut buffer)?;
    if read == 0 {
        return Ok(None);
    }
    if buffer.last() == Some(&b'\n') {
        let text = String::from_utf8_lossy(&buffer);
        return Ok(Some(Line::Complete(
            text.trim_end_matches(['\n', '\r']).to_string(),
            read as u64,
        )));
    }
    if buffer.len() <= MAX_LINE_BYTES {
        // The end of the file, and no end of line: it is being written.
        return Ok(None);
    }
    let mut skipped = read as u64;
    loop {
        let available = reader.fill_buf()?;
        if available.is_empty() {
            return Ok(None);
        }
        match available.iter().position(|byte| *byte == b'\n') {
            Some(position) => {
                reader.consume(position + 1);
                return Ok(Some(Line::TooLong(skipped + position as u64 + 1)));
            }
            None => {
                let length = available.len();
                reader.consume(length);
                skipped += length as u64;
            }
        }
    }
}

/// The complete lines of a file from a position, a batch at most. A line without its end (it is being written) is left.
fn read_batch(path: &Path, offset: u64) -> io::Result<Read> {
    let mut file = File::open(path)?;
    file.seek(SeekFrom::Start(offset))?;
    let mut reader = BufReader::new(file);
    let mut lines = Vec::new();
    let mut bytes = 0u64;
    let mut size = 0usize;
    while lines.len() < MAX_BATCH_LINES && size < MAX_BATCH_BYTES {
        match next_line(&mut reader)? {
            Some(Line::Complete(text, taken)) => {
                size += text.len();
                bytes += taken;
                lines.push(text);
            }
            Some(Line::TooLong(taken)) => {
                size += DROPPED_LINE.len();
                bytes += taken;
                lines.push(DROPPED_LINE.to_string());
            }
            None => break,
        }
    }
    Ok(Read { lines, bytes })
}

/// A fingerprint of the first line of a file (FNV-1a over its first 256 bytes): what says that a file is still the one that was sent.
fn fingerprint(path: &Path) -> Option<u64> {
    let mut start = Vec::new();
    BufReader::new(File::open(path).ok()?)
        .take(256)
        .read_until(b'\n', &mut start)
        .ok()?;
    if start.is_empty() {
        return None;
    }
    Some(start.iter().fold(0xcbf2_9ce4_8422_2325_u64, |hash, byte| {
        (hash ^ u64::from(*byte)).wrapping_mul(0x0100_0000_01b3)
    }))
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
        applications: RefCell<Vec<String>>,
        down: RefCell<Option<UploadError>>,
    }

    impl Fake {
        fn new() -> Self {
            Self {
                taken: RefCell::new(Vec::new()),
                calls: RefCell::new(Vec::new()),
                applications: RefCell::new(Vec::new()),
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
            self.applications
                .borrow_mut()
                .push(batch.application.to_string());
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

        assert_eq!(*fake.applications.borrow(), vec!["mnemarr"]);
    }

    #[test]
    fn should_name_the_application_from_a_windows_directory_too() {
        let lines = vec![r#"{"cwd":"C:\\work\\Otter"}"#.to_string()];

        assert_eq!(application_of(&lines).as_deref(), Some("Otter"));
    }

    #[test]
    fn should_resume_from_the_position_of_a_server_that_has_less_than_the_collector_thinks() {
        let workspace = Workspace::new("less");
        fs::write(
            workspace.file(),
            "{\"a\":1}\n{\"b\":2}\n{\"c\":3}\n{\"d\":4}\n",
        )
        .unwrap();
        let fake = Fake::new();
        fake.taken
            .borrow_mut()
            .extend(["{\"a\":1}".to_string(), "{\"b\":2}".to_string()]);
        let collector = collector(&workspace, &fake);
        // The collector believes it has sent three lines: the server has two.
        let mut state = State::default();
        let (offset, lines) = offset_after(&workspace.file(), 3);
        state.files.insert(
            workspace.file().to_string_lossy().into_owned(),
            FileState {
                offset,
                lines,
                application: None,
                head: 0,
            },
        );

        let report = collector.pass(&mut state);

        assert_eq!(
            *fake.taken.borrow(),
            vec!["{\"a\":1}", "{\"b\":2}", "{\"c\":3}", "{\"d\":4}"]
        );
        assert_eq!(report.lines_sent, 2);
        assert_eq!(state.files.values().next().unwrap().lines, 4);
    }

    #[test]
    fn should_send_a_long_file_in_batches_of_at_most_the_limit() {
        let workspace = Workspace::new("batches");
        let lines: String = (0..=MAX_BATCH_LINES)
            .map(|n| format!("{{\"n\":{n}}}\n"))
            .collect();
        fs::write(workspace.file(), lines).unwrap();
        let fake = Fake::new();
        let collector = collector(&workspace, &fake);
        let mut state = State::default();

        collector.pass(&mut state);

        assert_eq!(
            *fake.calls.borrow(),
            vec![(0, MAX_BATCH_LINES), (MAX_BATCH_LINES as u64, 1)]
        );
    }

    #[test]
    fn should_start_again_when_a_file_is_replaced_by_a_shorter_one_and_read_windows_line_ends() {
        let workspace = Workspace::new("short");
        fs::write(workspace.file(), "{\"a\":1}\r\n{\"b\":2}\r\n").unwrap();
        let fake = Fake::new();
        let collector = collector(&workspace, &fake);
        let mut state = State::default();
        collector.pass(&mut state);
        // The file is replaced by a shorter one.
        fs::write(workspace.file(), "{\"z\":9}\n").unwrap();

        let report = collector.pass(&mut state);

        assert_eq!(*fake.taken.borrow(), vec!["{\"a\":1}", "{\"b\":2}"]);
        assert_eq!(report.lines_sent, 0);
        // The file holds one line now: that is as far as there is to go.
        assert_eq!(state.files.values().next().unwrap().lines, 1);
    }

    #[cfg(unix)]
    #[test]
    fn should_say_which_file_could_not_be_read_and_go_on() {
        use std::os::unix::fs::PermissionsExt;
        let workspace = Workspace::new("unreadable");
        fs::write(workspace.file(), "{\"a\":1}\n").unwrap();
        fs::set_permissions(workspace.file(), fs::Permissions::from_mode(0o000)).unwrap();
        let fake = Fake::new();
        let collector = collector(&workspace, &fake);
        let mut state = State::default();

        let report = collector.pass(&mut state);

        // A user that is root can read anything: the test then has nothing to say.
        if report.unreadable.is_empty() {
            return;
        }
        assert_eq!(report.unreadable.len(), 1);
        assert!(report.stopped.is_none());
    }

    #[test]
    fn should_not_hold_a_line_that_is_too_long_and_keep_the_positions_of_the_lines_after_it() {
        let workspace = Workspace::new("huge");
        let huge = "x".repeat(MAX_LINE_BYTES * 3);
        fs::write(
            workspace.file(),
            format!("{{\"a\":1}}\n{huge}\n{{\"c\":3}}\n"),
        )
        .unwrap();
        let fake = Fake::new();
        let collector = collector(&workspace, &fake);
        let mut state = State::default();

        let report = collector.pass(&mut state);

        assert_eq!(
            *fake.taken.borrow(),
            vec!["{\"a\":1}", DROPPED_LINE, "{\"c\":3}"]
        );
        assert_eq!(report.lines_sent, 3);
        let size = fs::metadata(workspace.file()).unwrap().len();
        assert_eq!(state.files.values().next().unwrap().offset, size);
    }

    #[test]
    fn should_leave_a_too_long_line_that_has_no_end_yet_for_the_next_pass() {
        let workspace = Workspace::new("hugecut");
        fs::write(
            workspace.file(),
            format!("{{\"a\":1}}\n{}", "x".repeat(MAX_LINE_BYTES * 2)),
        )
        .unwrap();
        let fake = Fake::new();
        let collector = collector(&workspace, &fake);
        let mut state = State::default();

        collector.pass(&mut state);

        assert_eq!(*fake.taken.borrow(), vec!["{\"a\":1}"]);
    }

    #[test]
    fn should_start_again_when_a_file_is_replaced_by_another_of_the_same_size() {
        let workspace = Workspace::new("same-size");
        fs::write(workspace.file(), "{\"a\":1}\n{\"b\":2}\n").unwrap();
        let fake = Fake::new();
        let collector = collector(&workspace, &fake);
        let mut state = State::default();
        collector.pass(&mut state);
        // Another conversation, with the same size.
        fs::write(workspace.file(), "{\"x\":9}\n{\"y\":8}\n").unwrap();
        fake.taken.borrow_mut().clear();

        let report = collector.pass(&mut state);

        assert_eq!(report.lines_sent, 2);
        assert_eq!(*fake.taken.borrow(), vec!["{\"x\":9}", "{\"y\":8}"]);
    }

    #[test]
    fn should_stop_with_a_file_when_the_server_keeps_giving_a_position_that_gets_no_nearer() {
        struct Stuck(RefCell<u32>);
        impl Uploader for &Stuck {
            fn send(&self, _batch: &Batch<'_>) -> Result<Reply, UploadError> {
                *self.0.borrow_mut() += 1;
                Ok(Reply {
                    next: 0,
                    accepted: 0,
                })
            }
        }
        let workspace = Workspace::new("stuck");
        fs::write(workspace.file(), "{\"a\":1}\n").unwrap();
        let stuck = Stuck(RefCell::new(0));
        let collector = Collector::new("pc".to_string(), workspace.0.clone(), &stuck);
        let mut state = State::default();

        collector.pass(&mut state);

        assert!(
            *stuck.0.borrow() <= 2,
            "the same batch is not sent thousands of times"
        );
    }

    #[test]
    fn should_forget_the_files_that_are_gone() {
        let workspace = Workspace::new("prune");
        fs::write(workspace.file(), "{\"a\":1}\n").unwrap();
        let fake = Fake::new();
        let collector = collector(&workspace, &fake);
        let mut state = State::default();
        state
            .files
            .insert("/gone/old.jsonl".to_string(), FileState::default());

        collector.pass(&mut state);

        assert_eq!(state.files.len(), 1);
        assert!(!state.files.contains_key("/gone/old.jsonl"));
    }
}
