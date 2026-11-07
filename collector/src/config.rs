//! What the collector runs with: what was given, else the environment, else the usual places.

use std::path::PathBuf;
use std::time::Duration;

use crate::destinations;
use crate::{Options, UsageError};

/// Everything a pass needs, for one Mnemarr.
#[derive(PartialEq, Eq)]
pub struct Config {
    /// What this destination is called here (the host of its address, unless a person named it).
    pub name: String,
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
            .field("name", &self.name)
            .field("server", &self.server)
            .field("key", &"<hidden>")
            .field("machine", &self.machine)
            .field("projects", &self.projects)
            .field("state", &self.state)
            .field("interval", &self.interval)
            .finish()
    }
}

/// The rule of the server for the name of a machine (and of a session): a hostname with a space or an accent would make every batch fail.
pub fn is_machine_name(name: &str) -> bool {
    let mut characters = name.chars();
    characters.next().is_some_and(|c| c.is_ascii_alphanumeric())
        && name.len() <= 50
        && name
            .chars()
            .all(|c| c.is_ascii_alphanumeric() || matches!(c, '.' | '_' | '-'))
}

/// Whether an address is on this machine: the only place a key may be sent without being encrypted.
pub fn is_local(server: &str) -> bool {
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

/// The place of the files of the collector (the key, the small file that says where Mnemarr is): the usual place of the configuration of a user.
pub fn config_dir(
    options_dir: Option<&str>,
    get: &dyn Fn(&str) -> Option<String>,
) -> Option<PathBuf> {
    if let Some(dir) = options_dir {
        return Some(PathBuf::from(dir));
    }
    get("XDG_CONFIG_HOME")
        .map(PathBuf::from)
        .or_else(|| get("APPDATA").map(PathBuf::from))
        .or_else(|| {
            get("HOME")
                .or_else(|| get("USERPROFILE"))
                .map(|home| PathBuf::from(home).join(".config"))
        })
        .map(|folder| folder.join("mnemarr-collector"))
}

/// The name of the file that holds the key of this machine, in the folder of the collector.
pub const KEY_FILE: &str = "key";
/// The name of the small file that says where Mnemarr is and what this machine is called, in the folder of the collector.
pub const BOOTSTRAP_FILE: &str = "collector.json";

/// Whether an address may be given the key or the code: HTTPS, or a plain address that is on this machine.
/// A plain `http://` address is accepted for this machine itself, or when the person said so (`--allow-insecure-http`, which `enroll` keeps).
pub fn check_server(server: &str, allow_insecure_http: bool) -> Result<(), UsageError> {
    if server.starts_with("https://")
        || (server.starts_with("http://") && (allow_insecure_http || is_local(server)))
    {
        Ok(())
    } else {
        Err(UsageError(
            "the address of Mnemarr must be https:// (http:// only for this machine, or with --allow-insecure-http): the key is sent with every batch".to_string(),
        ))
    }
}

/// The name of this machine when nothing says it: its host name.
pub fn default_machine(
    get: &dyn Fn(&str) -> Option<String>,
    read: &dyn Fn(&str) -> Option<String>,
) -> Option<String> {
    get("HOSTNAME")
        .or_else(|| get("COMPUTERNAME"))
        .or_else(|| read("/etc/hostname").map(|name| name.trim().to_string()))
        .filter(|value| !value.is_empty())
}

/// What `enroll` left in the folder of the collector: where Mnemarr is, the name of this machine, and whether a plain `http://` address was accepted.
fn bootstrap(
    dir: Option<&PathBuf>,
    read: &dyn Fn(&str) -> Option<String>,
) -> (Option<String>, Option<String>, bool) {
    let Some(text) = dir.and_then(|dir| read(&dir.join(BOOTSTRAP_FILE).to_string_lossy())) else {
        return (None, None, false);
    };
    let Ok(value) = serde_json::from_str::<serde_json::Value>(&text) else {
        return (None, None, false);
    };
    let field = |name: &str| {
        value
            .get(name)
            .and_then(serde_json::Value::as_str)
            .map(str::to_string)
            .filter(|text| !text.is_empty())
    };
    let insecure = value
        .get("insecure")
        .and_then(serde_json::Value::as_bool)
        .unwrap_or(false);
    (field("server"), field("machine"), insecure)
}

/// The conversations of Claude Code: where it was said, else under the home of the user.
fn projects_dir(options: &Options, home: Option<&PathBuf>) -> Result<PathBuf, UsageError> {
    match &options.projects {
        Some(path) => Ok(PathBuf::from(path)),
        None => home
            .map(|home| home.join(".claude").join("projects"))
            .ok_or_else(|| {
                UsageError(
                    "the conversations of Claude Code cannot be found: give --projects".to_string(),
                )
            }),
    }
}

/// The folder where the positions are kept: the usual place of the state of a user.
pub fn state_dir(get: &dyn Fn(&str) -> Option<String>, home: Option<&PathBuf>) -> Option<PathBuf> {
    get("XDG_STATE_HOME")
        .map(PathBuf::from)
        .or_else(|| get("LOCALAPPDATA").map(PathBuf::from))
        .or_else(|| home.map(|home| home.join(".local").join("state")))
        .map(|folder| folder.join("mnemarr-collector"))
}

/// Whether a destination can be used: its address is allowed, its machine name is one the server accepts, and its key can be read. The key is given back.
pub fn check_destination(
    dir: &std::path::Path,
    destination: &destinations::Destination,
) -> Result<String, UsageError> {
    check_server(&destination.server, destination.insecure)?;
    if !is_machine_name(&destination.machine) {
        return Err(UsageError(format!(
            "'{}' is not a name of machine that the server accepts",
            destination.machine
        )));
    }
    destinations::key_of(dir, destination).ok_or_else(|| {
        UsageError(format!(
            "the key of {} cannot be read: link it again",
            destination.name
        ))
    })
}

/// What each destination that cannot be used says is wrong with it, by name: one that is left out of the passes is never left out in silence.
pub fn problems(options: &Options, get: &dyn Fn(&str) -> Option<String>) -> Vec<(String, String)> {
    let Some(dir) = config_dir(options.config_dir.as_deref(), get) else {
        return Vec::new();
    };
    destinations::load(&dir)
        .iter()
        .filter_map(|destination| {
            check_destination(&dir, destination)
                .err()
                .map(|error| (destination.name.clone(), error.0))
        })
        .collect()
}

/// Everything the passes need, for each Mnemarr this machine sends to (ADR 0030). When an address or a key is given (on the command line or in the
/// environment) it is one destination of its own and the list is left alone, as `run --server …` always was; otherwise it is the list kept in the
/// folder of the collector, read again at each pass so that linking another is taken into account without a restart. A destination whose address is
/// not allowed, or whose key is gone, is left out and the others go on.
pub fn resolve_all(
    options: &Options,
    get: &dyn Fn(&str) -> Option<String>,
    read: &dyn Fn(&str) -> Option<String>,
) -> Result<Vec<Config>, UsageError> {
    let explicit = options.server.is_some()
        || options.key_file.is_some()
        || get("MNEMARR_SERVER").is_some()
        || get("MNEMARR_COLLECTOR_KEY").is_some();
    if explicit {
        return resolve(options, get, read).map(|config| vec![config]);
    }
    let Some(dir) = config_dir(options.config_dir.as_deref(), get) else {
        return Err(UsageError(
            "the folder of the collector cannot be found: give --config-dir".to_string(),
        ));
    };
    // A list that cannot be read is said as that, not as "not linked": the person is sent to the right cause.
    let listed =
        destinations::load_for_change(&dir).map_err(|error| UsageError(error.to_string()))?;
    if listed.is_empty() {
        return Err(UsageError(
            "this machine is not linked to a Mnemarr: run `mnemarr-collector link`".to_string(),
        ));
    }
    let home = get("HOME")
        .or_else(|| get("USERPROFILE"))
        .map(PathBuf::from);
    let projects = projects_dir(options, home.as_ref())?;
    let states = state_dir(get, home.as_ref());
    let interval = Duration::from_secs(options.interval_seconds.unwrap_or(30).max(1));
    let mut configs = Vec::new();
    let mut first_error = None;
    for destination in listed {
        let outcome = (|| {
            let key = check_destination(&dir, &destination)?;
            let state = match (&options.state, &states) {
                (Some(path), _) if configs.is_empty() => PathBuf::from(path),
                (_, Some(folder)) => folder.join(&destination.state_file),
                _ => {
                    return Err(UsageError(
                        "the place to remember the position cannot be found: give --state"
                            .to_string(),
                    ));
                }
            };
            Ok(Config {
                name: destination.name.clone(),
                server: destination.server.clone(),
                key,
                machine: destination.machine.clone(),
                projects: projects.clone(),
                state,
                interval,
            })
        })();
        match outcome {
            Ok(config) => configs.push(config),
            Err(error) => first_error = first_error.or(Some(error)),
        }
    }
    match (configs.is_empty(), first_error) {
        (true, Some(error)) => Err(error),
        _ => Ok(configs),
    }
}

/// Reads what is not given from the environment (`get`) and from the files the options name (`read`), so that a test needs neither.
/// What is not given and not in the environment is what `enroll` left in the folder of the collector.
pub fn resolve(
    options: &Options,
    get: &dyn Fn(&str) -> Option<String>,
    read: &dyn Fn(&str) -> Option<String>,
) -> Result<Config, UsageError> {
    let dir = config_dir(options.config_dir.as_deref(), get);
    let (bootstrap_server, bootstrap_machine, bootstrap_insecure) = bootstrap(dir.as_ref(), read);
    let server = options
        .server
        .clone()
        .or_else(|| get("MNEMARR_SERVER"))
        .or(bootstrap_server)
        .filter(|value| !value.is_empty())
        .ok_or_else(|| {
            UsageError(
                "the address of Mnemarr is needed: --server, MNEMARR_SERVER, or `mnemarr-collector enroll`".to_string(),
            )
        })?;
    // The key is sent in a header: over HTTPS, or to this machine.
    check_server(&server, options.allow_insecure_http || bootstrap_insecure)?;
    let key = match &options.key_file {
        Some(path) => read(path)
            .map(|text| text.trim().to_string())
            .filter(|text| !text.is_empty())
            .ok_or_else(|| UsageError(format!("the key file {path} cannot be read or is empty")))?,
        None => get("MNEMARR_COLLECTOR_KEY")
            .or_else(|| {
                dir.as_ref()
                    .and_then(|dir| read(&dir.join(KEY_FILE).to_string_lossy()))
                    .map(|text| text.trim().to_string())
            })
            .filter(|value| !value.is_empty())
            .ok_or_else(|| {
                UsageError(
                    "the ingest key is needed: --key-file, MNEMARR_COLLECTOR_KEY, or `mnemarr-collector enroll`".to_string(),
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
        .or(bootstrap_machine)
        .or_else(|| default_machine(get, read))
        .filter(|value| !value.is_empty())
        .ok_or_else(|| {
            UsageError(
                "the name of this machine is needed: --machine or MNEMARR_MACHINE".to_string(),
            )
        })?;
    if !is_machine_name(&machine) {
        return Err(UsageError(format!(
            "'{machine}' is not a name of machine that the server accepts: letters, digits, dots, hyphens and underscores, up to 50 characters, starting with a letter or a digit. Give another with --machine or MNEMARR_MACHINE"
        )));
    }
    let projects = projects_dir(options, home.as_ref())?;
    let state = match &options.state {
        Some(path) => PathBuf::from(path),
        None => state_dir(get, home.as_ref())
            .map(|folder| folder.join("state.json"))
            .ok_or_else(|| {
                UsageError(
                    "the place to remember the position cannot be found: give --state".to_string(),
                )
            })?,
    };
    Ok(Config {
        name: destinations::name_for(&server),
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

    #[test]
    fn should_call_local_only_an_address_on_this_machine() {
        for local in [
            "http://localhost:8080",
            "http://127.0.0.1",
            "http://127.0.0.1:8384/api",
            "http://[::1]:8384",
        ] {
            assert!(is_local(local), "{local}");
        }
        for elsewhere in [
            "http://localhost.evil.example",
            "http://127.0.0.1.example.com",
            "http://x/127.0.0.1",
            "http://mnemarr.example",
            "http://192.168.1.10:8384",
        ] {
            assert!(!is_local(elsewhere), "{elsewhere}");
        }
    }

    fn linked(name: &str) -> (std::path::PathBuf, impl Fn(&str) -> Option<String>) {
        let dir =
            std::env::temp_dir().join(format!("mnemarr-config-{name}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        let map: HashMap<String, String> = [
            ("HOME", "/home/me".to_string()),
            ("XDG_CONFIG_HOME", dir.to_str().unwrap().to_string()),
            ("XDG_STATE_HOME", "/state".to_string()),
        ]
        .into_iter()
        .map(|(key, value)| (key.to_string(), value))
        .collect();
        (dir.join("mnemarr-collector"), move |key: &str| {
            map.get(key).cloned()
        })
    }

    #[test]
    fn should_make_a_configuration_for_each_destination_with_its_own_positions() {
        let (dir, get) = linked("two");
        destinations::add(
            &dir,
            "https://prod.example",
            "pc-prod",
            false,
            None,
            "k-prod",
        )
        .unwrap();
        destinations::add(
            &dir,
            "http://dev.lan:8384",
            "pc-dev",
            true,
            Some("dev"),
            "k-dev",
        )
        .unwrap();

        let configs = resolve_all(&Options::default(), &get, &|_| None).unwrap();

        assert_eq!(configs.len(), 2);
        assert_eq!(
            configs
                .iter()
                .map(|config| (
                    config.name.as_str(),
                    config.machine.as_str(),
                    config.key.as_str()
                ))
                .collect::<Vec<_>>(),
            vec![
                ("prod.example", "pc-prod", "k-prod"),
                ("dev", "pc-dev", "k-dev")
            ]
        );
        assert_ne!(configs[0].state, configs[1].state);
        assert!(configs[0].state.starts_with("/state/mnemarr-collector"));
        assert_eq!(
            configs[0].projects,
            PathBuf::from("/home/me/.claude/projects")
        );
    }

    #[test]
    fn should_leave_the_list_alone_when_an_address_or_a_key_is_given() {
        let (dir, get) = linked("explicit");
        destinations::add(
            &dir,
            "https://prod.example",
            "pc-prod",
            false,
            None,
            "k-prod",
        )
        .unwrap();
        let options = Options {
            server: Some("https://one-off.example".to_string()),
            key_file: Some("/k".to_string()),
            machine: Some("one-off".to_string()),
            ..Options::default()
        };

        let configs = resolve_all(&options, &get, &|path| {
            (path == "/k").then(|| "key".to_string())
        })
        .unwrap();

        assert_eq!(configs.len(), 1);
        assert_eq!(configs[0].server, "https://one-off.example");
    }

    #[test]
    fn should_say_to_link_when_the_machine_has_no_destination() {
        let (_, get) = linked("none");

        let error = resolve_all(&Options::default(), &get, &|_| None).unwrap_err();

        assert!(error.0.contains("link"), "{error}");
    }

    #[test]
    fn should_leave_out_a_destination_that_cannot_be_used_and_go_on_with_the_others() {
        let (dir, get) = linked("partial");
        destinations::add(&dir, "https://prod.example", "pc", false, None, "k-prod").unwrap();
        let broken =
            destinations::add(&dir, "https://broken.example", "pc", false, None, "k").unwrap();
        std::fs::remove_file(dir.join(&broken.key_file)).unwrap();

        let configs = resolve_all(&Options::default(), &get, &|_| None).unwrap();

        assert_eq!(configs.len(), 1);
        assert_eq!(configs[0].name, "prod.example");
        destinations::remove(&dir, "prod.example").unwrap();
        let error = resolve_all(&Options::default(), &get, &|_| None).unwrap_err();
        assert!(error.0.contains("key of broken.example"), "{error}");
    }

    #[test]
    fn should_refuse_a_plain_http_destination_that_was_not_accepted() {
        let (dir, get) = linked("insecure");
        destinations::add(&dir, "http://nas.lan:8384", "pc", false, None, "k").unwrap();

        let error = resolve_all(&Options::default(), &get, &|_| None).unwrap_err();

        assert!(error.0.contains("https"), "{error}");
    }

    #[test]
    fn should_refuse_a_name_of_machine_that_the_server_would_refuse() {
        assert!(
            is_machine_name(&"a".repeat(50)),
            "the longest name there is"
        );
        for name in ["my pc", "pc\u{e9}", "-pc", "", &"a".repeat(51)] {
            let get = environment(&[
                ("MNEMARR_SERVER", "https://m.example"),
                ("MNEMARR_COLLECTOR_KEY", "k"),
                ("HOME", "/h"),
            ]);
            let options = Options {
                machine: Some(name.to_string()),
                ..Options::default()
            };

            let error = resolve(&options, &get, &|_| None).unwrap_err();

            assert!(error.0.contains("--machine"), "{name:?}: {}", error.0);
        }
    }

    fn files(pairs: &[(&str, &str)]) -> impl Fn(&str) -> Option<String> {
        let map: HashMap<String, String> = pairs
            .iter()
            .map(|(path, text)| ((*path).to_string(), (*text).to_string()))
            .collect();
        move |path| map.get(&path.replace('\\', "/")).cloned()
    }

    #[test]
    fn should_find_the_address_the_machine_and_the_key_that_enroll_left_in_the_folder_of_the_collector()
     {
        let get = environment(&[("XDG_CONFIG_HOME", "/cfg"), ("HOME", "/h")]);
        let read = files(&[
            (
                "/cfg/mnemarr-collector/collector.json",
                r#"{"server":"https://mnemarr.example","machine":"laptop"}"#,
            ),
            ("/cfg/mnemarr-collector/key", "mnm_enrolled\n"),
        ]);

        let config = resolve(&Options::default(), &get, &read).unwrap();

        assert_eq!(config.server, "https://mnemarr.example");
        assert_eq!(config.machine, "laptop");
        assert_eq!(config.key, "mnm_enrolled");
    }

    #[test]
    fn should_prefer_what_is_given_to_what_enroll_left() {
        let get = environment(&[
            ("XDG_CONFIG_HOME", "/cfg"),
            ("HOME", "/h"),
            ("MNEMARR_SERVER", "https://other.example"),
            ("MNEMARR_COLLECTOR_KEY", "mnm_from_env"),
            ("MNEMARR_MACHINE", "from-env"),
        ]);
        let read = files(&[
            (
                "/cfg/mnemarr-collector/collector.json",
                r#"{"server":"https://mnemarr.example","machine":"laptop"}"#,
            ),
            ("/cfg/mnemarr-collector/key", "mnm_enrolled"),
        ]);

        let config = resolve(&Options::default(), &get, &read).unwrap();

        assert_eq!(config.server, "https://other.example");
        assert_eq!(config.machine, "from-env");
        assert_eq!(config.key, "mnm_from_env");
    }

    #[test]
    fn should_look_in_the_folder_it_is_given_and_ignore_a_file_that_is_not_what_enroll_writes() {
        let get = environment(&[("HOME", "/h"), ("MNEMARR_MACHINE", "pc")]);
        let read = files(&[
            (
                "/mine/collector.json",
                r#"{"server":"https://mnemarr.example"}"#,
            ),
            ("/mine/key", "mnm_mine"),
            ("/h/.config/mnemarr-collector/collector.json", "not json"),
        ]);
        let options = Options {
            config_dir: Some("/mine".to_string()),
            ..Options::default()
        };

        let config = resolve(&options, &get, &read).unwrap();

        assert_eq!(config.server, "https://mnemarr.example");
        assert_eq!(config.key, "mnm_mine");
        assert_eq!(
            config_dir(None, &get),
            Some(PathBuf::from("/h/.config/mnemarr-collector"))
        );
        let broken = files(&[("/h/.config/mnemarr-collector/collector.json", "not json")]);
        assert!(resolve(&Options::default(), &get, &broken).is_err());
    }

    #[test]
    fn should_look_for_the_folder_in_the_places_of_each_system() {
        assert_eq!(
            config_dir(
                None,
                &environment(&[("APPDATA", "C:/Users/me/AppData/Roaming")])
            ),
            Some(PathBuf::from(
                "C:/Users/me/AppData/Roaming/mnemarr-collector"
            ))
        );
        assert_eq!(
            config_dir(None, &environment(&[("USERPROFILE", "C:/Users/me")])),
            Some(PathBuf::from("C:/Users/me/.config/mnemarr-collector"))
        );
        assert_eq!(config_dir(None, &environment(&[])), None);
    }

    #[test]
    fn should_accept_plain_http_for_another_machine_only_when_the_person_said_so() {
        assert!(check_server("http://192.168.1.10:8384", false).is_err());
        assert!(check_server("http://192.168.1.10:8384", true).is_ok());
        assert!(check_server("http://localhost:8384", false).is_ok());
        assert!(check_server("ftp://mnemarr.example", true).is_err());
        assert!(check_server("https://mnemarr.example", false).is_ok());
    }

    #[test]
    fn should_keep_the_permission_for_plain_http_that_enroll_was_given() {
        let get = environment(&[
            ("XDG_CONFIG_HOME", "/cfg"),
            ("HOME", "/h"),
            ("MNEMARR_MACHINE", "pc"),
        ]);
        let said = files(&[
            (
                "/cfg/mnemarr-collector/collector.json",
                r#"{"server":"http://192.168.1.10:8384","machine":"pc","insecure":true}"#,
            ),
            ("/cfg/mnemarr-collector/key", "mnm_k"),
        ]);
        let not_said = files(&[
            (
                "/cfg/mnemarr-collector/collector.json",
                r#"{"server":"http://192.168.1.10:8384","machine":"pc"}"#,
            ),
            ("/cfg/mnemarr-collector/key", "mnm_k"),
        ]);

        assert!(resolve(&Options::default(), &get, &said).is_ok());
        assert!(resolve(&Options::default(), &get, &not_said).is_err());
    }
}
