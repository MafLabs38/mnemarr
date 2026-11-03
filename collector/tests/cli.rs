//! Runs the real binary, as the CI smoke test and a user would.

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
}
