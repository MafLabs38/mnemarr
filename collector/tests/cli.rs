//! Runs the real binary, as the CI smoke test and a user would.
#![allow(
    clippy::unwrap_used,
    reason = "this whole file is tests: its helpers too"
)]

use std::process::Command;

fn collector() -> Command {
    Command::new(env!("CARGO_BIN_EXE_mnemarr-collector"))
}

#[test]
fn should_print_its_version() {
    let output = collector().arg("--version").output().unwrap();

    assert!(output.status.success());
    let text = String::from_utf8(output.stdout).unwrap();
    assert!(text.starts_with("mnemarr-collector "));
}

#[test]
fn should_exit_with_a_usage_error_on_an_unknown_argument() {
    let output = collector().arg("--nope").output().unwrap();

    assert_eq!(output.status.code(), Some(2));
    let text = String::from_utf8(output.stderr).unwrap();
    assert!(text.contains("unexpected argument: --nope"));
}

#[test]
fn should_print_the_usage_on_stdout_and_succeed_when_asked_for_help() {
    let output = collector().arg("--help").output().unwrap();

    assert!(output.status.success());
    let text = String::from_utf8(output.stdout).unwrap();
    assert!(text.contains("USAGE:"));
    assert!(text.contains("--version"));
    assert!(text.contains("--help"));
}

#[cfg(unix)]
#[test]
fn should_report_a_usage_error_and_not_panic_on_an_argument_that_is_not_utf8() {
    use std::os::unix::ffi::OsStringExt;

    let output = collector()
        .arg(std::ffi::OsString::from_vec(vec![0xff, 0xfe]))
        .output()
        .unwrap();

    assert_eq!(output.status.code(), Some(2));
    let text = String::from_utf8(output.stderr).unwrap();
    assert!(text.contains("unexpected argument"));
}

#[cfg(unix)]
#[test]
fn should_succeed_when_the_reader_of_its_output_has_gone() {
    use std::process::Stdio;

    // The reader closes its end at once, the way `--help | head -n 0` does: writing then fails with a broken pipe.
    let mut child = collector()
        .arg("--help")
        .stdout(Stdio::piped())
        .spawn()
        .unwrap();
    drop(child.stdout.take());

    assert!(child.wait().unwrap().success());
}

/// A folder of the collector as `enroll` leaves it, with a server that nobody answers for: what the background collector needs to start.
struct Joined {
    dir: std::path::PathBuf,
}

impl Joined {
    fn new(name: &str) -> Self {
        let dir = std::env::temp_dir().join(format!("mnemarr-cli-{name}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        std::fs::write(
            dir.join("collector.json"),
            r#"{"server":"http://127.0.0.1:9","machine":"cli-test"}"#,
        )
        .unwrap();
        std::fs::write(dir.join("key"), "mnm_not-a-real-key").unwrap();
        Self { dir }
    }

    /// The program, with its own folder as the home of the user: what it reads and keeps is in the test's folder.
    fn run(&self, args: &[&str]) -> std::process::Output {
        collector()
            .args(args)
            .args(["--config-dir", self.dir.to_str().unwrap()])
            .env("HOME", &self.dir)
            .env("USERPROFILE", &self.dir)
            .env("XDG_STATE_HOME", &self.dir)
            .env("LOCALAPPDATA", &self.dir)
            .output()
            .unwrap()
    }
}

impl Drop for Joined {
    fn drop(&mut self) {
        let _ = self.run(&["stop"]);
        let _ = std::fs::remove_dir_all(&self.dir);
    }
}

fn said(output: &std::process::Output) -> String {
    String::from_utf8_lossy(&output.stderr).to_string() + &String::from_utf8_lossy(&output.stdout)
}

#[test]
fn should_start_in_the_background_and_hand_the_terminal_back_say_it_runs_and_stop_when_asked() {
    let joined = Joined::new("background");
    assert!(said(&joined.run(&["status"])).contains("running: no"));

    let started = joined.run(&["start"]);

    assert!(started.status.success(), "{}", said(&started));
    assert!(said(&started).contains("running in the background"));
    let status = said(&joined.run(&["status"]));
    assert!(status.contains("running: yes"), "{status}");
    assert!(status.contains("machine: cli-test"), "{status}");
    assert!(said(&joined.run(&["start"])).contains("already running"));
    // What a collector in the background says is in its log: it has no terminal.
    let log = joined.dir.join("collector.log");
    let mut text = String::new();
    for _ in 0..50 {
        text = std::fs::read_to_string(&log).unwrap_or_default();
        if text.contains("as cli-test") {
            break;
        }
        std::thread::sleep(std::time::Duration::from_millis(100));
    }
    assert!(text.contains("as cli-test"), "{text}");

    let stopped = joined.run(&["stop"]);

    assert!(said(&stopped).contains("stopped"), "{}", said(&stopped));
    assert!(said(&joined.run(&["status"])).contains("running: no"));
    assert!(said(&joined.run(&["stop"])).contains("not running"));
}

#[test]
fn should_refuse_to_run_in_the_background_a_machine_that_has_not_joined() {
    let dir = std::env::temp_dir().join(format!("mnemarr-cli-notjoined-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&dir);
    std::fs::create_dir_all(&dir).unwrap();

    let output = collector()
        .args(["start", "--config-dir", dir.to_str().unwrap()])
        .env("HOME", &dir)
        .env_remove("MNEMARR_SERVER")
        .env_remove("MNEMARR_COLLECTOR_KEY")
        .output()
        .unwrap();

    assert_eq!(output.status.code(), Some(2));
    assert!(said(&output).contains("enroll"));
    let _ = std::fs::remove_dir_all(&dir);
}

#[test]
fn should_let_a_second_collector_leave_the_first_alone() {
    let joined = Joined::new("second");
    assert!(joined.run(&["start"]).status.success());

    let second = joined.run(&["run"]);

    assert!(second.status.success());
    assert!(
        said(&second).contains("already running"),
        "{}",
        said(&second)
    );
    assert!(said(&joined.run(&["status"])).contains("running: yes"));
}

#[test]
fn should_run_as_ever_when_it_carries_a_setting_and_is_given_something_to_do() {
    use mnemarr_collector::embedded::{Embedded, append};
    let dir = std::env::temp_dir().join(format!("mnemarr-cli-carry-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&dir);
    std::fs::create_dir_all(&dir).unwrap();
    let program = std::fs::read(env!("CARGO_BIN_EXE_mnemarr-collector")).unwrap();
    let carrying = dir.join(if cfg!(windows) {
        "carrying.exe"
    } else {
        "carrying"
    });
    std::fs::write(
        &carrying,
        append(
            &program,
            &Embedded {
                server: "https://mnemarr.example".to_string(),
                machine: "pc".to_string(),
                code: "ABCD-EFGH-JKLM".to_string(),
                insecure: false,
            },
        ),
    )
    .unwrap();
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        std::fs::set_permissions(&carrying, std::fs::Permissions::from_mode(0o700)).unwrap();
    }

    let output = Command::new(&carrying).arg("--version").output().unwrap();

    assert!(output.status.success());
    assert!(
        String::from_utf8(output.stdout)
            .unwrap()
            .starts_with("mnemarr-collector ")
    );
    let _ = std::fs::remove_dir_all(&dir);
}

#[test]
fn should_pause_for_a_while_or_until_resumed_and_say_so_in_its_status() {
    let joined = Joined::new("pause");
    assert!(said(&joined.run(&["status"])).contains("paused: no"));

    let paused = joined.run(&["pause", "--minutes", "30"]);

    assert!(paused.status.success(), "{}", said(&paused));
    assert!(said(&paused).contains("paused for 30 minute(s)"));
    assert!(said(&joined.run(&["status"])).contains("paused: yes"));
    assert!(said(&joined.run(&["resume"])).contains("resumed"));
    assert!(said(&joined.run(&["status"])).contains("paused: no"));

    assert!(said(&joined.run(&["pause"])).contains("until you resume it"));
    assert!(said(&joined.run(&["status"])).contains("paused: yes"));
    assert!(joined.run(&["resume"]).status.success());
    assert!(
        said(&joined.run(&["resume"])).contains("resumed"),
        "resuming when not paused changes nothing and is not an error"
    );
}

// ---- Linking to several Mnemarr (ADR 0030) -------------------------------------------------------------------------------------------------------------

use std::io::{Read, Write};
use std::net::TcpListener;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};

/// A Mnemarr, as far as the collector can tell: it gives a key for a code, takes batches and signs of life, and lets a person approve a request to be linked
/// after one more question. It keeps every request it got.
struct FakeMnemarr {
    address: String,
    requests: Arc<Mutex<Vec<(String, String, String)>>>,
    stop: Arc<AtomicBool>,
}

impl FakeMnemarr {
    fn start() -> Self {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        listener.set_nonblocking(true).unwrap();
        let port = listener.local_addr().unwrap().port();
        let requests = Arc::new(Mutex::new(Vec::new()));
        let stop = Arc::new(AtomicBool::new(false));
        let (kept, ending) = (requests.clone(), stop.clone());
        std::thread::spawn(move || {
            let mut polls = 0;
            while !ending.load(Ordering::SeqCst) {
                let Ok((mut stream, _)) = listener.accept() else {
                    std::thread::sleep(std::time::Duration::from_millis(10));
                    continue;
                };
                stream.set_nonblocking(false).unwrap();
                let mut data = Vec::new();
                let mut chunk = [0u8; 4096];
                let (path, key, body) = loop {
                    let read = stream.read(&mut chunk).unwrap_or(0);
                    data.extend_from_slice(&chunk[..read]);
                    let text = String::from_utf8_lossy(&data).to_string();
                    if let Some((head, rest)) = text.split_once("\r\n\r\n") {
                        let length = head
                            .lines()
                            .find_map(|line| {
                                line.to_ascii_lowercase()
                                    .strip_prefix("content-length: ")
                                    .and_then(|v| v.trim().parse::<usize>().ok())
                            })
                            .unwrap_or(0);
                        if rest.len() >= length || read == 0 {
                            let path = head.split_whitespace().nth(1).unwrap_or("").to_string();
                            let key = head
                                .lines()
                                .find_map(|line| {
                                    let lower = line.to_ascii_lowercase();
                                    lower
                                        .strip_prefix("x-api-key: ")
                                        .map(|value| value.trim().to_string())
                                })
                                .unwrap_or_default();
                            break (path, key, rest.to_string());
                        }
                    }
                    if read == 0 {
                        break (String::new(), String::new(), String::new());
                    }
                };
                kept.lock().unwrap().push((path.clone(), key, body.clone()));
                let (status, answer) = match path.as_str() {
                    "/api/v1/ingest/enrol" => (
                        "200 OK",
                        format!(
                            r#"{{"key":"key-{port}","machine":"pc-{port}","kind":"intermittent"}}"#
                        ),
                    ),
                    "/api/v1/ingest/conversations" => {
                        let value: serde_json::Value =
                            serde_json::from_str(&body).unwrap_or_default();
                        let from = value["from"].as_u64().unwrap_or(0);
                        let count = value["lines"].as_array().map_or(0, Vec::len) as u64;
                        (
                            "200 OK",
                            format!(r#"{{"next":{},"accepted":{count}}}"#, from + count),
                        )
                    }
                    "/api/v1/ingest/heartbeat" => ("200 OK", "{}".to_string()),
                    "/api/v1/ingest/pair" => (
                        "201 Created",
                        format!(
                            r#"{{"userCode":"ABCD-EFGH-JKLM","deviceCode":"dvc_secret","verificationUrl":"http://127.0.0.1:{port}/link?code=ABCD-EFGH-JKLM","expiresInSeconds":60,"intervalSeconds":1}}"#
                        ),
                    ),
                    "/api/v1/ingest/pair/poll" => {
                        polls += 1;
                        if polls < 2 {
                            ("200 OK", r#"{"state":"pending"}"#.to_string())
                        } else {
                            (
                                "200 OK",
                                format!(
                                    r#"{{"state":"approved","key":"key-{port}","machine":"pc-{port}","kind":"intermittent"}}"#
                                ),
                            )
                        }
                    }
                    _ => ("404 Not Found", "{}".to_string()),
                };
                let reply = format!(
                    "HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{answer}",
                    answer.len()
                );
                let _ = stream.write_all(reply.as_bytes());
            }
        });
        Self {
            address: format!("http://127.0.0.1:{port}"),
            requests,
            stop,
        }
    }

    fn port(&self) -> String {
        self.address.rsplit(':').next().unwrap().to_string()
    }

    /// The batches it received: the machine they were for, the key that carried them, and the lines.
    fn batches(&self) -> Vec<(String, String, Vec<String>)> {
        self.requests
            .lock()
            .unwrap()
            .iter()
            .filter(|(path, _, _)| path == "/api/v1/ingest/conversations")
            .map(|(_, key, body)| {
                let value: serde_json::Value = serde_json::from_str(body).unwrap();
                (
                    value["machine"].as_str().unwrap().to_string(),
                    key.clone(),
                    value["lines"]
                        .as_array()
                        .unwrap()
                        .iter()
                        .map(|line| line.as_str().unwrap().to_string())
                        .collect(),
                )
            })
            .collect()
    }

    fn heartbeats(&self) -> usize {
        self.requests
            .lock()
            .unwrap()
            .iter()
            .filter(|(path, _, _)| path == "/api/v1/ingest/heartbeat")
            .count()
    }
}

impl Drop for FakeMnemarr {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::SeqCst);
    }
}

/// A computer with a collector that is not linked to anything, and a conversation of Claude Code on it.
struct Computer {
    home: std::path::PathBuf,
}

impl Computer {
    fn new(name: &str) -> Self {
        let home = std::env::temp_dir().join(format!("mnemarr-pc-{name}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&home);
        let project = home.join(".claude").join("projects").join("some-project");
        std::fs::create_dir_all(&project).unwrap();
        std::fs::write(
            project.join("0a0a0a0a-1111-2222-3333-444444444444.jsonl"),
            concat!(
                r#"{"type":"user","timestamp":"2026-11-06T10:00:00Z","message":{"role":"user","content":"hello there"}}"#,
                "\n",
                r#"{"type":"assistant","timestamp":"2026-11-06T10:00:05Z","message":{"role":"assistant","content":"hello back"}}"#,
                "\n"
            ),
        )
        .unwrap();
        Self { home }
    }

    fn run(&self, args: &[&str]) -> std::process::Output {
        collector()
            .args(args)
            .args(["--config-dir", self.home.join("config").to_str().unwrap()])
            .env("HOME", &self.home)
            .env("USERPROFILE", &self.home)
            .env("XDG_STATE_HOME", self.home.join("state"))
            .env("LOCALAPPDATA", self.home.join("state"))
            .env_remove("MNEMARR_SERVER")
            .env_remove("MNEMARR_COLLECTOR_KEY")
            .env_remove("HOSTNAME")
            .env("COMPUTERNAME", "test-pc")
            .output()
            .unwrap()
    }

    fn link(&self, mnemarr: &FakeMnemarr) -> std::process::Output {
        self.run(&[
            "link",
            "--server",
            &mnemarr.address,
            "--code",
            "AAAA-BBBB-CCCC",
            "--allow-insecure-http",
        ])
    }
}

impl Drop for Computer {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.home);
    }
}

#[test]
fn should_send_every_conversation_to_every_mnemarr_it_is_linked_to_each_with_its_own_key_and_name_for_the_machine()
 {
    let computer = Computer::new("both");
    let (development, production) = (FakeMnemarr::start(), FakeMnemarr::start());
    assert!(computer.link(&development).status.success());
    assert!(computer.link(&production).status.success());

    let listed = said(&computer.run(&["destinations"]));
    assert!(
        listed.contains(&format!("pc-{}", development.port())),
        "{listed}"
    );
    assert!(
        listed.contains(&format!("pc-{}", production.port())),
        "{listed}"
    );

    let once = computer.run(&["once"]);
    assert!(once.status.success(), "{}", said(&once));

    for mnemarr in [&development, &production] {
        let batches = mnemarr.batches();
        assert_eq!(batches.len(), 1, "one conversation, one batch each");
        assert_eq!(
            batches[0].0,
            format!("pc-{}", mnemarr.port()),
            "its own name for this machine"
        );
        assert_eq!(
            batches[0].1,
            format!("key-{}", mnemarr.port()),
            "its own key"
        );
        assert_eq!(batches[0].2.len(), 2);
        assert!(batches[0].2[0].contains("hello there"));
        assert!(mnemarr.heartbeats() >= 1, "each hears from the machine");
    }
    // Nothing is sent twice: the positions are kept for each.
    assert!(computer.run(&["once"]).status.success());
    assert_eq!(
        (development.batches().len(), production.batches().len()),
        (1, 1)
    );
}

#[test]
fn should_go_on_sending_to_the_other_mnemarr_when_one_cannot_be_reached_and_say_that_the_pass_was_not_done()
 {
    let computer = Computer::new("one-down");
    let (down, up) = (FakeMnemarr::start(), FakeMnemarr::start());
    assert!(computer.link(&down).status.success());
    assert!(computer.link(&up).status.success());
    down.stop.store(true, Ordering::SeqCst);
    std::thread::sleep(std::time::Duration::from_millis(200));
    let address = down.address.clone();

    let once = computer.run(&["once"]);

    assert_eq!(
        once.status.code(),
        Some(1),
        "one of them was not reached: {}",
        said(&once)
    );
    assert_eq!(up.batches().len(), 1, "the other one has its conversation");
    assert!(said(&once).contains(&address[7..]) || said(&once).contains("127.0.0.1"));
}

#[test]
fn should_link_by_asking_show_the_address_to_open_wait_for_the_approval_and_send_there() {
    let computer = Computer::new("asking");
    let mnemarr = FakeMnemarr::start();

    let output = computer.run(&[
        "link",
        "--server",
        &mnemarr.address,
        "--no-browser",
        "--allow-insecure-http",
    ]);

    let text = said(&output);
    assert!(output.status.success(), "{text}");
    assert!(text.contains("code is ABCD-EFGH-JKLM"), "{text}");
    assert!(
        text.contains(&format!("{}/link?code=ABCD-EFGH-JKLM", mnemarr.address)),
        "{text}"
    );
    assert!(text.contains("linked to"), "{text}");
    assert!(computer.run(&["once"]).status.success());
    assert_eq!(mnemarr.batches().len(), 1);
    assert_eq!(mnemarr.batches()[0].1, format!("key-{}", mnemarr.port()));
    let asked = mnemarr.requests.lock().unwrap();
    assert!(
        asked
            .iter()
            .any(|(path, _, body)| path == "/api/v1/ingest/pair" && body.contains("test-pc")),
        "it says what it is called"
    );
}

#[test]
fn should_ask_to_be_linked_even_when_the_name_of_the_computer_is_not_a_name_of_machine_because_a_person_chooses_the_name()
 {
    let computer = Computer::new("spaces");
    let mnemarr = FakeMnemarr::start();

    let output = collector()
        .args([
            "link",
            "--server",
            &mnemarr.address,
            "--no-browser",
            "--allow-insecure-http",
        ])
        .args([
            "--config-dir",
            computer.home.join("config").to_str().unwrap(),
        ])
        .env("HOME", &computer.home)
        .env("USERPROFILE", &computer.home)
        .env("XDG_STATE_HOME", computer.home.join("state"))
        .env_remove("HOSTNAME")
        .env("COMPUTERNAME", "Gaming PC (de Maf)")
        .output()
        .unwrap();

    assert!(output.status.success(), "{}", said(&output));
    let asked = mnemarr.requests.lock().unwrap();
    assert!(
        asked
            .iter()
            .any(|(path, _, body)| path == "/api/v1/ingest/pair"
                && body.contains("Gaming PC (de Maf)")),
        "it suggests the name the computer has, as it is: Mnemarr cleans it and a person chooses the name"
    );
}

#[test]
fn should_unlink_a_mnemarr_and_stop_sending_to_it_and_say_when_there_is_none_of_that_name() {
    let computer = Computer::new("unlink");
    let (development, production) = (FakeMnemarr::start(), FakeMnemarr::start());
    assert!(computer.link(&development).status.success());
    assert!(computer.link(&production).status.success());
    let name = "127.0.0.1".to_string();
    let second = format!("{name}-2");

    let missing = computer.run(&["unlink", "nothing"]);
    assert_eq!(missing.status.code(), Some(1));
    let nameless = computer.run(&["unlink"]);
    assert_eq!(nameless.status.code(), Some(2));
    assert!(
        said(&nameless).contains(&second),
        "it lists the names: {}",
        said(&nameless)
    );
    let removed = computer.run(&["unlink", &name]);
    assert!(removed.status.success(), "{}", said(&removed));

    assert!(computer.run(&["once"]).status.success());
    assert_eq!(
        (development.batches().len(), production.batches().len()),
        (0, 1),
        "only the one that is left"
    );
}

#[test]
fn should_read_the_folder_of_an_earlier_collector_as_one_destination() {
    let joined = Joined::new("legacy-destinations");

    let listed = said(&joined.run(&["destinations"]));

    assert!(listed.contains("cli-test"), "{listed}");
    assert!(listed.contains("127.0.0.1:9"), "{listed}");
}

#[test]
fn should_keep_running_with_nothing_linked_so_that_a_person_can_link_it_from_the_icon() {
    let dir = std::env::temp_dir().join(format!("mnemarr-unlinked-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&dir);
    std::fs::create_dir_all(&dir).unwrap();
    let mut child = std::process::Command::new(env!("CARGO_BIN_EXE_mnemarr-collector"))
        .args(["run", "--no-tray", "--config-dir"])
        .arg(&dir)
        .env_remove("MNEMARR_SERVER")
        .env_remove("MNEMARR_COLLECTOR_KEY")
        .stdin(std::process::Stdio::null())
        .stdout(std::process::Stdio::null())
        .stderr(std::process::Stdio::piped())
        .spawn()
        .unwrap();
    std::thread::sleep(std::time::Duration::from_millis(1500));

    let still_running = child.try_wait().unwrap().is_none();
    child.kill().unwrap();
    let mut said = String::new();
    std::io::Read::read_to_string(child.stderr.as_mut().unwrap(), &mut said).unwrap();
    let _ = child.wait();
    let _ = std::fs::remove_dir_all(&dir);

    assert!(still_running, "it ended: {said}");
    assert!(said.contains("not linked to any Mnemarr yet"), "{said}");
}
