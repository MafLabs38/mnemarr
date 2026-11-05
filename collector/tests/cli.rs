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
