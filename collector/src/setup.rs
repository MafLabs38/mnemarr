//! What a downloaded program does when it is opened (ADR 0027 §15): it puts a copy of itself where the collector lives, joins the machine with the code
//! it carries, and has the copy run in the background. The person has done one thing: open a file.

use std::fs;
use std::io;
use std::path::{Path, PathBuf};

use crate::embedded::Embedded;
use crate::enrol::{self, Enrolled};
use crate::upload::UploadError;

/// Why a setup did not end.
#[derive(Debug)]
pub enum SetupError {
    /// The program could not be put in place.
    Copy(io::Error),
    /// The code was refused, or the server could not be reached.
    Join(UploadError),
    /// It joined, but the key could not be kept.
    Keep(io::Error),
    /// It joined, but could not be set to run in the background.
    Install(String),
}

impl std::fmt::Display for SetupError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Copy(error) => write!(f, "the program could not be put in its place: {error}"),
            Self::Join(UploadError::Refused(_, reason)) => write!(
                f,
                "this machine could not join: {reason} Ask for a new one on the Machines page of Mnemarr."
            ),
            Self::Join(error) => write!(f, "this machine could not join: {error}"),
            Self::Keep(error) => write!(
                f,
                "this machine joined, but its key could not be kept: {error}. The code is used: ask for another."
            ),
            Self::Install(reason) => write!(
                f,
                "this machine joined, but the collector could not be installed: {reason}"
            ),
        }
    }
}

impl std::error::Error for SetupError {}

/// Where the program lives once installed: its own folder, in the data of the user.
pub fn install_dir(get: &dyn Fn(&str) -> Option<String>) -> Option<PathBuf> {
    get("LOCALAPPDATA")
        .map(PathBuf::from)
        .or_else(|| get("XDG_DATA_HOME").map(PathBuf::from))
        .or_else(|| {
            get("HOME")
                .or_else(|| get("USERPROFILE"))
                .map(|home| PathBuf::from(home).join(".local").join("share"))
        })
        .map(|dir| dir.join("mnemarr-collector"))
}

/// The name of the installed program.
pub fn program_name() -> &'static str {
    if cfg!(windows) {
        "mnemarr-collector.exe"
    } else {
        "mnemarr-collector"
    }
}

/// Puts `program` (the bytes of this program without its setting) in `dir`, joins the machine with what the setting says, keeps the key in
/// `config_dir`, and has `install` make the copy run in the background. `stop` is what ends a collector that already runs from there, so that
/// its file can be replaced.
pub fn run(
    setting: &Embedded,
    program: &[u8],
    dir: &Path,
    config_dir: &Path,
    stop: &dyn Fn(),
    install: &dyn Fn(&Path) -> Result<(), String>,
) -> Result<Enrolled, SetupError> {
    let target = dir.join(program_name());
    stop();
    fs::create_dir_all(dir).map_err(SetupError::Copy)?;
    write_program(&target, program).map_err(SetupError::Copy)?;
    let enrolled = enrol::exchange(&setting.server, &setting.code, &setting.machine)
        .map_err(SetupError::Join)?;
    enrol::save(config_dir, &setting.server, &enrolled, setting.insecure)
        .map_err(SetupError::Keep)?;
    install(&target).map_err(SetupError::Install)?;
    Ok(enrolled)
}

fn write_program(target: &Path, program: &[u8]) -> io::Result<()> {
    // Written beside and moved into place: a program that runs is not half overwritten.
    let temporary = target.with_extension("new");
    fs::write(&temporary, program)?;
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        fs::set_permissions(&temporary, fs::Permissions::from_mode(0o700))?;
    }
    fs::rename(&temporary, target)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::cell::RefCell;
    use std::io::{Read, Write};
    use std::net::TcpListener;

    fn serve_once(status: &str, body: &str) -> (String, std::thread::JoinHandle<String>) {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let address = format!("http://{}", listener.local_addr().unwrap());
        let answer = format!(
            "HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",
            body.len()
        );
        let handle = std::thread::spawn(move || {
            let (mut stream, _) = listener.accept().unwrap();
            let mut request = Vec::new();
            let mut chunk = [0u8; 4096];
            loop {
                let read = stream.read(&mut chunk).unwrap();
                request.extend_from_slice(&chunk[..read]);
                if String::from_utf8_lossy(&request).contains("\"machine\"") || read == 0 {
                    break;
                }
            }
            stream.write_all(answer.as_bytes()).unwrap();
            String::from_utf8_lossy(&request).to_string()
        });
        (address, handle)
    }

    fn folder(name: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!("mnemarr-setup-{name}-{}", std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        dir
    }

    fn setting(server: &str) -> Embedded {
        Embedded {
            server: server.to_string(),
            machine: "gaming-pc".to_string(),
            code: "ABCD-EFGH-JKLM".to_string(),
            insecure: true,
        }
    }

    #[test]
    fn should_put_the_program_in_place_join_with_the_code_it_carries_keep_the_key_and_install() {
        let (address, handle) = serve_once(
            "200 OK",
            r#"{"key":"mnm_the-key","machine":"gaming-pc","kind":"intermittent"}"#,
        );
        let base = folder("ok");
        let (dir, config) = (base.join("program"), base.join("config"));
        let calls = RefCell::new(Vec::new());

        let enrolled = run(
            &setting(&address),
            b"the program",
            &dir,
            &config,
            &|| calls.borrow_mut().push("stop".to_string()),
            &|path| {
                calls
                    .borrow_mut()
                    .push(format!("install {}", path.display()));
                Ok(())
            },
        )
        .unwrap();

        assert_eq!(enrolled.machine, "gaming-pc");
        assert_eq!(fs::read(dir.join(program_name())).unwrap(), b"the program");
        assert_eq!(
            fs::read_to_string(config.join("key")).unwrap(),
            "mnm_the-key"
        );
        let said = fs::read_to_string(config.join("collector.json")).unwrap();
        assert!(said.contains("\"insecure\": true"), "{said}");
        let request = handle.join().unwrap();
        assert!(request.contains("ABCD-EFGH-JKLM") && request.contains("gaming-pc"));
        assert_eq!(
            calls.borrow()[0],
            "stop",
            "what runs is stopped before its file is replaced"
        );
        assert!(calls.borrow()[1].starts_with("install "));
        let _ = fs::remove_dir_all(&base);
    }

    #[test]
    fn should_say_what_the_server_said_when_the_code_is_refused_and_install_nothing() {
        let (address, handle) = serve_once(
            "400 Bad Request",
            r#"{"title":"The code is not valid: it may be wrong, used, expired or not approved yet."}"#,
        );
        let base = folder("refused");
        let installed = RefCell::new(false);

        let error = run(
            &setting(&address),
            b"p",
            &base.join("program"),
            &base.join("config"),
            &|| {},
            &|_| {
                *installed.borrow_mut() = true;
                Ok(())
            },
        )
        .unwrap_err();

        handle.join().unwrap();
        assert!(error.to_string().contains("could not join"));
        assert!(error.to_string().contains("not valid"));
        assert!(error.to_string().contains("Machines page"));
        assert!(!*installed.borrow());
        assert!(!base.join("config").join("key").exists());
        let _ = fs::remove_dir_all(&base);
    }

    #[test]
    fn should_say_when_it_joined_but_could_not_be_installed() {
        let (address, handle) = serve_once(
            "200 OK",
            r#"{"key":"mnm_k","machine":"gaming-pc","kind":"intermittent"}"#,
        );
        let base = folder("install");

        let error = run(
            &setting(&address),
            b"p",
            &base.join("program"),
            &base.join("config"),
            &|| {},
            &|_| Err("no way to start it".to_string()),
        )
        .unwrap_err();

        handle.join().unwrap();
        assert!(
            error
                .to_string()
                .contains("joined, but the collector could not be installed")
        );
        let _ = fs::remove_dir_all(&base);
    }

    #[test]
    fn should_find_the_folder_of_the_program_in_the_data_of_the_user_on_each_system() {
        let get = |pairs: &'static [(&'static str, &'static str)]| {
            move |name: &str| {
                pairs
                    .iter()
                    .find(|(key, _)| *key == name)
                    .map(|(_, value)| (*value).to_string())
            }
        };
        assert_eq!(
            install_dir(&get(&[("LOCALAPPDATA", "C:/Users/me/AppData/Local")])),
            Some(PathBuf::from("C:/Users/me/AppData/Local/mnemarr-collector"))
        );
        assert_eq!(
            install_dir(&get(&[("HOME", "/home/me")])),
            Some(PathBuf::from("/home/me/.local/share/mnemarr-collector"))
        );
        assert_eq!(install_dir(&get(&[])), None);
    }
}
