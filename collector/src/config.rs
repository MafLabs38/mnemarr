//! What the collector runs with: what was given, else the environment, else the usual places.

use std::path::PathBuf;
use std::time::Duration;

use crate::{Options, UsageError};

/// Everything a pass needs.
#[derive(PartialEq, Eq)]
pub struct Config {
    pub server: String,
    pub key: String,
    pub machine: String,
    pub projects: PathBuf,
    pub state: PathBuf,
    pub interval: Duration,
}

/// The key is never written where a log or a panic could carry it.
impl std::fmt::Debug for Config {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Config")
            .field("server", &self.server)
            .field("key", &"<hidden>")
            .field("machine", &self.machine)
            .field("projects", &self.projects)
            .field("state", &self.state)
            .field("interval", &self.interval)
            .finish()
    }
}

/// Whether an address is on this machine: the only place a key may be sent without being encrypted.
fn is_local(server: &str) -> bool {
    let host = server
        .strip_prefix("http://")
        .unwrap_or(server)
        .split(['/', '?'])
        .next()
        .unwrap_or("");
    let host = host
        .strip_prefix('[')
        .map_or(host, |inner| inner.split(']').next().unwrap_or(inner));
    let host = host.rsplit_once(':').map_or(host, |(name, port)| {
        if port.chars().all(|c| c.is_ascii_digit()) && !name.contains(':') {
            name
        } else {
            host
        }
    });
    matches!(host, "localhost" | "127.0.0.1" | "::1")
}

/// Reads what is not given from the environment (`get`) and from the files the options name (`read`), so that a test needs neither.
pub fn resolve(
    options: &Options,
    get: &dyn Fn(&str) -> Option<String>,
    read: &dyn Fn(&str) -> Option<String>,
) -> Result<Config, UsageError> {
    let server = options
        .server
        .clone()
        .or_else(|| get("MNEMARR_SERVER"))
        .filter(|value| !value.is_empty())
        .ok_or_else(|| {
            UsageError("the address of Mnemarr is needed: --server or MNEMARR_SERVER".to_string())
        })?;
    // The key is sent in a header: over HTTPS, or to this machine.
    if !server.starts_with("https://") && !(server.starts_with("http://") && is_local(&server)) {
        return Err(UsageError(
            "the address of Mnemarr must be https:// (http:// only for this machine): the key is sent with every batch".to_string(),
        ));
    }
    let key = match &options.key_file {
        Some(path) => read(path)
            .map(|text| text.trim().to_string())
            .filter(|text| !text.is_empty())
            .ok_or_else(|| UsageError(format!("the key file {path} cannot be read or is empty")))?,
        None => get("MNEMARR_COLLECTOR_KEY")
            .filter(|value| !value.is_empty())
            .ok_or_else(|| {
                UsageError(
                    "the ingest key is needed: --key-file or MNEMARR_COLLECTOR_KEY".to_string(),
                )
            })?,
    };
    let home = get("HOME")
        .or_else(|| get("USERPROFILE"))
        .map(PathBuf::from);
    let machine = options
        .machine
        .clone()
        .or_else(|| get("MNEMARR_MACHINE"))
        .or_else(|| get("HOSTNAME"))
        .or_else(|| get("COMPUTERNAME"))
        .or_else(|| read("/etc/hostname").map(|name| name.trim().to_string()))
        .filter(|value| !value.is_empty())
        .ok_or_else(|| {
            UsageError(
                "the name of this machine is needed: --machine or MNEMARR_MACHINE".to_string(),
            )
        })?;
    let projects = match &options.projects {
        Some(path) => PathBuf::from(path),
        None => home
            .clone()
            .map(|home| home.join(".claude").join("projects"))
            .ok_or_else(|| {
                UsageError(
                    "the conversations of Claude Code cannot be found: give --projects".to_string(),
                )
            })?,
    };
    let state = match &options.state {
        Some(path) => PathBuf::from(path),
        None => get("XDG_STATE_HOME")
            .map(PathBuf::from)
            .or_else(|| get("LOCALAPPDATA").map(PathBuf::from))
            .or_else(|| home.map(|home| home.join(".local").join("state")))
            .map(|folder| folder.join("mnemarr-collector").join("state.json"))
            .ok_or_else(|| {
                UsageError(
                    "the place to remember the position cannot be found: give --state".to_string(),
                )
            })?,
    };
    Ok(Config {
        server,
        key,
        machine,
        projects,
        state,
        interval: Duration::from_secs(options.interval_seconds.unwrap_or(30).max(1)),
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::HashMap;

    fn environment(pairs: &[(&str, &str)]) -> impl Fn(&str) -> Option<String> {
        let map: HashMap<String, String> = pairs
            .iter()
            .map(|(name, value)| ((*name).to_string(), (*value).to_string()))
            .collect();
        move |name| map.get(name).cloned()
    }

    #[test]
    fn should_take_the_usual_places_when_only_the_server_and_the_key_are_given() {
        let get = environment(&[
            ("HOME", "/home/me"),
            ("HOSTNAME", "pc"),
            ("MNEMARR_SERVER", "https://m.example"),
        ]);
        let read = |path: &str| (path == "/etc/key").then(|| "secret\n".to_string());
        let options = Options {
            key_file: Some("/etc/key".to_string()),
            ..Options::default()
        };

        let config = resolve(&options, &get, &read).unwrap();

        assert_eq!(config.server, "https://m.example");
        assert_eq!(config.key, "secret");
        assert_eq!(config.machine, "pc");
        assert_eq!(config.projects, PathBuf::from("/home/me/.claude/projects"));
        assert_eq!(
            config.state,
            PathBuf::from("/home/me/.local/state/mnemarr-collector/state.json")
        );
        assert_eq!(config.interval, Duration::from_secs(30));
    }

    #[test]
    fn should_prefer_what_is_given_to_what_the_environment_says() {
        let get = environment(&[
            ("HOME", "/home/me"),
            ("MNEMARR_SERVER", "https://env.example"),
            ("MNEMARR_COLLECTOR_KEY", "k"),
        ]);
        let options = Options {
            server: Some("https://arg.example".to_string()),
            machine: Some("laptop".to_string()),
            projects: Some("/p".to_string()),
            state: Some("/s.json".to_string()),
            interval_seconds: Some(5),
            ..Options::default()
        };

        let config = resolve(&options, &get, &|_| None).unwrap();

        assert_eq!(
            (
                config.server.as_str(),
                config.machine.as_str(),
                config.interval
            ),
            ("https://arg.example", "laptop", Duration::from_secs(5))
        );
        assert_eq!(
            (config.projects, config.state),
            (PathBuf::from("/p"), PathBuf::from("/s.json"))
        );
    }

    #[test]
    fn should_say_what_is_missing() {
        let nothing = environment(&[]);
        let key_only = environment(&[("MNEMARR_COLLECTOR_KEY", "k")]);

        let no_server = resolve(&Options::default(), &key_only, &|_| None).unwrap_err();
        let no_key = resolve(
            &Options {
                server: Some("https://s".to_string()),
                ..Options::default()
            },
            &nothing,
            &|_| None,
        )
        .unwrap_err();
        let empty_key = resolve(
            &Options {
                server: Some("https://s".to_string()),
                key_file: Some("/k".to_string()),
                ..Options::default()
            },
            &nothing,
            &|_| Some("  \n".to_string()),
        )
        .unwrap_err();

        assert!(no_server.0.contains("--server"));
        assert!(no_key.0.contains("--key-file"));
        assert!(empty_key.0.contains("/k"));
    }

    #[test]
    fn should_refuse_a_server_that_is_not_https_unless_it_is_this_machine() {
        let get = environment(&[
            ("MNEMARR_COLLECTOR_KEY", "k"),
            ("HOME", "/h"),
            ("MNEMARR_MACHINE", "pc"),
        ]);
        for refused in [
            "http://mnemarr.example",
            "ftp://x",
            "mnemarr.example",
            "http://127.0.0.1.example.com",
        ] {
            let options = Options {
                server: Some(refused.to_string()),
                ..Options::default()
            };
            assert!(resolve(&options, &get, &|_| None).is_err(), "{refused}");
        }
        for accepted in [
            "https://mnemarr.example",
            "http://localhost:8384",
            "http://127.0.0.1:8383/x",
            "http://[::1]:8383",
        ] {
            let options = Options {
                server: Some(accepted.to_string()),
                ..Options::default()
            };
            assert!(resolve(&options, &get, &|_| None).is_ok(), "{accepted}");
        }
    }

    #[test]
    fn should_never_show_the_key_when_the_configuration_is_printed() {
        let get = environment(&[
            ("MNEMARR_SERVER", "https://m.example"),
            ("MNEMARR_COLLECTOR_KEY", "super-secret-key"),
            ("HOME", "/h"),
            ("MNEMARR_MACHINE", "pc"),
        ]);

        let config = resolve(&Options::default(), &get, &|_| None).unwrap();

        assert!(!format!("{config:?}").contains("super-secret-key"));
    }

    #[test]
    fn should_use_the_other_places_for_the_machine_and_the_state_and_keep_the_wait_at_one_second_at_least()
     {
        let get = environment(&[
            ("MNEMARR_SERVER", "https://m.example"),
            ("MNEMARR_COLLECTOR_KEY", "k"),
            ("USERPROFILE", "C:\\Users\\me"),
            ("COMPUTERNAME", "WIN-PC"),
            ("LOCALAPPDATA", "C:\\Users\\me\\AppData\\Local"),
            ("XDG_STATE_HOME", "/xdg"),
        ]);
        let options = Options {
            interval_seconds: Some(0),
            ..Options::default()
        };

        let config = resolve(&options, &get, &|_| None).unwrap();

        assert_eq!(config.machine, "WIN-PC");
        assert_eq!(
            config.state,
            PathBuf::from("/xdg")
                .join("mnemarr-collector")
                .join("state.json")
        );
        assert_eq!(
            config.projects,
            PathBuf::from("C:\\Users\\me")
                .join(".claude")
                .join("projects")
        );
        assert_eq!(config.interval, Duration::from_secs(1));
    }

    #[test]
    fn should_read_the_name_of_the_machine_from_the_file_of_the_system_when_nothing_else_says_it() {
        let get = environment(&[
            ("MNEMARR_SERVER", "https://m.example"),
            ("MNEMARR_COLLECTOR_KEY", "k"),
            ("HOME", "/h"),
        ]);
        let read = |path: &str| (path == "/etc/hostname").then(|| "server-1\n".to_string());

        let config = resolve(&Options::default(), &get, &read).unwrap();

        assert_eq!(config.machine, "server-1");
    }
}
