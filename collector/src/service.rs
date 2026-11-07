//! Runs the collector in the background and has it start with the session of the user (ADR 0027 §14).
//!
//! A person installs it once and is not left with a window that has to stay open: `install` makes it start with the session and starts it now,
//! `start` and `stop` do what they say, `status` tells, `uninstall` takes it away. Nothing needs administrator rights: on Windows the entry is in
//! the user's own list of programs that start with their session (the one Task Manager and Settings show, and let the user switch off), on Linux
//! a unit of the user's own systemd.

use std::fs::{self, File, OpenOptions, TryLockError};
use std::io;
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::thread;
use std::time::Duration;

/// Where a running collector writes its process number, so that `stop` finds it.
pub const PID_FILE: &str = "collector.pid";
/// Held by the running collector for as long as it runs: a second one finds it taken and does not start.
pub const LOCK_FILE: &str = "collector.lock";
/// What a collector started in the background says.
pub const LOG_FILE: &str = "collector.log";
/// The log is started again when it is longer than this: a service that has nothing to say fills no disk.
const MAX_LOG_BYTES: u64 = 1_000_000;
const RUN_KEY: &str = r"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";
const RUN_VALUE: &str = "MnemarrCollector";
const UNIT_NAME: &str = "mnemarr-collector.service";

/// The lock of a collector that runs. Dropped when it stops, which is when another may start.
pub struct Instance {
    _lock: File,
}

/// Takes the lock of the folder, and writes the process number beside it. `None` when another collector holds it.
pub fn acquire(dir: &Path) -> io::Result<Option<Instance>> {
    fs::create_dir_all(dir)?;
    let lock = open_lock(dir)?;
    match lock.try_lock() {
        Ok(()) => {
            fs::write(dir.join(PID_FILE), std::process::id().to_string())?;
            Ok(Some(Instance { _lock: lock }))
        }
        Err(TryLockError::WouldBlock) => Ok(None),
        Err(TryLockError::Error(error)) => Err(error),
    }
}

fn open_lock(dir: &Path) -> io::Result<File> {
    OpenOptions::new()
        .create(true)
        .truncate(false)
        .write(true)
        .open(dir.join(LOCK_FILE))
}

/// Whether a collector runs for this folder: its lock is taken. Looking does not take it.
pub fn is_running(dir: &Path) -> bool {
    let Ok(lock) = open_lock(dir) else {
        return false;
    };
    match lock.try_lock() {
        Ok(()) => false,
        Err(TryLockError::WouldBlock) => true,
        Err(TryLockError::Error(_)) => false,
    }
}

/// The process number the running collector wrote.
pub fn pid(dir: &Path) -> Option<u32> {
    fs::read_to_string(dir.join(PID_FILE))
        .ok()
        .and_then(|text| text.trim().parse().ok())
}

/// What `start` did.
#[derive(Debug, PartialEq, Eq)]
pub enum Started {
    Started,
    AlreadyRunning,
    /// It was started and is not running: the log says why.
    Failed,
}

/// Starts the collector in the background, as this user: no window, nothing to keep open, what it says goes to the log of the folder.
pub fn start(dir: &Path, exe: &Path, custom_dir: Option<&str>) -> io::Result<Started> {
    if is_running(dir) {
        return Ok(Started::AlreadyRunning);
    }
    fs::create_dir_all(dir)?;
    let log_path = dir.join(LOG_FILE);
    if fs::metadata(&log_path).is_ok_and(|meta| meta.len() > MAX_LOG_BYTES) {
        fs::remove_file(&log_path)?;
    }
    let mut args = vec!["run".to_string()];
    if let Some(custom) = custom_dir {
        args.extend(["--config-dir".to_string(), custom.to_string()]);
    }
    args.extend([
        "--log-file".to_string(),
        log_path.to_string_lossy().into_owned(),
    ]);
    spawn_detached(exe, &args)?;
    // It takes the lock as it starts: it is running when the lock is taken.
    for _ in 0..80 {
        thread::sleep(Duration::from_millis(100));
        if is_running(dir) {
            return Ok(Started::Started);
        }
    }
    Ok(Started::Failed)
}

/// Runs a program in the background, tied to nothing of this process: no console window, and not to the window that started it (closing the terminal
/// does not stop it). On Windows a program started by this one inherits the handles of it, a pipe that something reads our output through included,
/// and whoever reads it would wait for the program to end: the shell is asked to start it instead, which passes nothing on.
#[cfg(windows)]
fn spawn_detached(exe: &Path, args: &[String]) -> io::Result<()> {
    use std::os::windows::process::CommandExt;
    const CREATE_NO_WINDOW: u32 = 0x0800_0000;
    let quoted = |text: &str| format!("'{}'", text.replace('\'', "''"));
    let argument_list = args
        .iter()
        .map(|arg| {
            if arg.contains(' ') {
                format!("\"{arg}\"")
            } else {
                arg.clone()
            }
        })
        .collect::<Vec<_>>()
        .join(" ");
    let script = format!(
        "Start-Process -FilePath {} -ArgumentList {} -WindowStyle Hidden",
        quoted(&exe.to_string_lossy()),
        quoted(&argument_list)
    );
    let status = Command::new("powershell")
        .args([
            "-NoProfile",
            "-NonInteractive",
            "-ExecutionPolicy",
            "Bypass",
            "-Command",
            &script,
        ])
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .creation_flags(CREATE_NO_WINDOW)
        .status()?;
    if status.success() {
        Ok(())
    } else {
        Err(io::Error::other("the shell could not start the program"))
    }
}

#[cfg(unix)]
fn spawn_detached(exe: &Path, args: &[String]) -> io::Result<()> {
    use std::os::unix::process::CommandExt;
    Command::new(exe)
        .args(args)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .process_group(0)
        .spawn()
        .map(|_| ())
}

/// Stops the collector that runs for this folder. False when none ran.
pub fn stop(dir: &Path) -> bool {
    if !is_running(dir) {
        return false;
    }
    let Some(pid) = pid(dir) else {
        return false;
    };
    let ended = if cfg!(windows) {
        Command::new("taskkill")
            .args(["/PID", &pid.to_string(), "/F"])
            .output()
    } else {
        Command::new("kill").arg(pid.to_string()).output()
    };
    if ended.is_err() {
        return false;
    }
    for _ in 0..30 {
        thread::sleep(Duration::from_millis(100));
        if !is_running(dir) {
            return true;
        }
    }
    false
}

/// What a person is told by `status`.
pub struct Status {
    pub running: Option<u32>,
    pub starts_with_session: bool,
    pub log: PathBuf,
}

pub fn status(dir: &Path) -> Status {
    Status {
        running: is_running(dir).then(|| pid(dir)).flatten(),
        starts_with_session: autostart_present(),
        log: dir.join(LOG_FILE),
    }
}

/// The unit of the user's systemd that runs the collector. Pure text, so that it is tried without a systemd.
pub fn unit_text(exe: &Path, custom_dir: Option<&str>) -> String {
    let config = custom_dir.map_or(String::new(), |dir| format!(" --config-dir \"{dir}\""));
    format!(
        "[Unit]\nDescription=Mnemarr collector\nAfter=network-online.target\n\n[Service]\nExecStart=\"{}\" run{config}\nRestart=on-failure\nRestartSec=30\nNice=10\nIOSchedulingClass=idle\n\n[Install]\nWantedBy=default.target\n",
        exe.display()
    )
}

/// The line the list of programs that start with a Windows session holds: it starts the collector in the background, and leaves.
pub fn run_key_value(exe: &Path, custom_dir: Option<&str>) -> String {
    let config = custom_dir.map_or(String::new(), |dir| format!(" --config-dir \"{dir}\""));
    format!("\"{}\" start{config}", exe.display())
}

/// How `install` went.
#[derive(Debug, PartialEq, Eq)]
pub enum Installed {
    /// It starts with the session, and it runs now.
    WithSession,
    /// It runs now, and nothing could be set to start it with the session on this system.
    RunningOnly(String),
}

/// Puts the entry that starts the collector with a Windows session in the list of the user, without starting it now. True when Windows took it.
pub fn register(exe: &Path, custom_dir: Option<&str>) -> io::Result<bool> {
    let value = run_key_value(exe, custom_dir);
    let added = Command::new("reg")
        .args([
            "add", RUN_KEY, "/v", RUN_VALUE, "/t", "REG_SZ", "/d", &value, "/f",
        ])
        .output()?;
    Ok(added.status.success())
}

/// Has the collector start with the session of the user, and starts it now.
pub fn install(dir: &Path, exe: &Path, custom_dir: Option<&str>) -> io::Result<Installed> {
    if cfg!(windows) {
        if !register(exe, custom_dir)? {
            return Ok(Installed::RunningOnly(
                "Windows did not take the entry that starts it with your session".to_string(),
            ));
        }
        start(dir, exe, custom_dir)?;
        Ok(Installed::WithSession)
    } else if cfg!(target_os = "linux") {
        install_systemd(dir, exe, custom_dir)
    } else {
        start(dir, exe, custom_dir)?;
        Ok(Installed::RunningOnly(
            "starting with the session is not set up on this system yet".to_string(),
        ))
    }
}

fn systemd_dir() -> Option<PathBuf> {
    std::env::var_os("XDG_CONFIG_HOME")
        .map(PathBuf::from)
        .or_else(|| std::env::var_os("HOME").map(|home| PathBuf::from(home).join(".config")))
        .map(|dir| dir.join("systemd").join("user"))
}

fn systemctl(args: &[&str]) -> bool {
    Command::new("systemctl")
        .arg("--user")
        .args(args)
        .output()
        .is_ok_and(|output| output.status.success())
}

fn install_systemd(dir: &Path, exe: &Path, custom_dir: Option<&str>) -> io::Result<Installed> {
    let Some(units) = systemd_dir() else {
        start(dir, exe, custom_dir)?;
        return Ok(Installed::RunningOnly(
            "no place for a user service was found".to_string(),
        ));
    };
    // One at a time: a collector started by hand is stopped before the service takes over.
    stop(dir);
    fs::create_dir_all(&units)?;
    fs::write(units.join(UNIT_NAME), unit_text(exe, custom_dir))?;
    if systemctl(&["daemon-reload"]) && systemctl(&["enable", "--now", UNIT_NAME]) {
        return Ok(Installed::WithSession);
    }
    // A machine with no user service manager (a container, a session without one): it still runs now.
    let _ = fs::remove_file(units.join(UNIT_NAME));
    start(dir, exe, custom_dir)?;
    Ok(Installed::RunningOnly(
        "this session has no user service manager (systemd --user)".to_string(),
    ))
}

/// Stops the collector and takes away what starts it with the session. The machine stays joined: its key and its address are kept.
pub fn uninstall(dir: &Path) -> io::Result<()> {
    if cfg!(windows) {
        let _ = Command::new("reg")
            .args(["delete", RUN_KEY, "/v", RUN_VALUE, "/f"])
            .output();
    } else if let (true, Some(units)) = (cfg!(target_os = "linux"), systemd_dir()) {
        systemctl(&["disable", "--now", UNIT_NAME]);
        let _ = fs::remove_file(units.join(UNIT_NAME));
        systemctl(&["daemon-reload"]);
    }
    stop(dir);
    Ok(())
}

fn autostart_present() -> bool {
    if cfg!(windows) {
        Command::new("reg")
            .args(["query", RUN_KEY, "/v", RUN_VALUE])
            .output()
            .is_ok_and(|output| output.status.success())
    } else if cfg!(target_os = "linux") {
        systemd_dir().is_some_and(|dir| dir.join(UNIT_NAME).exists())
    } else {
        false
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn folder(name: &str) -> PathBuf {
        let dir =
            std::env::temp_dir().join(format!("mnemarr-service-{name}-{}", std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        dir
    }

    #[test]
    fn should_let_one_collector_run_for_a_folder_and_free_the_place_when_it_ends() {
        let dir = folder("lock");

        let first = acquire(&dir).unwrap();
        assert!(first.is_some());
        assert!(is_running(&dir));
        assert_eq!(pid(&dir), Some(std::process::id()));
        assert!(
            acquire(&dir).unwrap().is_none(),
            "a second one does not start"
        );

        drop(first);
        assert!(!is_running(&dir));
        assert!(acquire(&dir).unwrap().is_some());
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn should_say_nothing_runs_for_a_folder_that_does_not_exist() {
        assert!(!is_running(&folder("none")));
        assert_eq!(pid(&folder("none")), None);
        assert!(!stop(&folder("none")));
    }

    #[test]
    fn should_write_the_unit_of_the_user_service_with_a_low_priority_and_a_restart() {
        let text = unit_text(
            Path::new("/home/me/.local/share/mnemarr-collector/mnemarr-collector"),
            None,
        );

        assert!(text.contains(
            "ExecStart=\"/home/me/.local/share/mnemarr-collector/mnemarr-collector\" run\n"
        ));
        assert!(text.contains("Restart=on-failure"));
        assert!(text.contains("Nice=10"));
        assert!(text.contains("IOSchedulingClass=idle"));
        assert!(text.contains("WantedBy=default.target"));
        assert!(
            unit_text(Path::new("/x/c"), Some("/my dir"))
                .contains("run --config-dir \"/my dir\"\n")
        );
    }

    #[test]
    fn should_write_the_entry_that_starts_it_with_a_windows_session_so_that_it_starts_and_leaves() {
        assert_eq!(
            run_key_value(
                Path::new(r"C:\Users\me\AppData\Local\mnemarr-collector\mnemarr-collector.exe"),
                None
            ),
            r#""C:\Users\me\AppData\Local\mnemarr-collector\mnemarr-collector.exe" start"#
        );
    }
}
