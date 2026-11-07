//! The Mnemarr instances this machine sends to (ADR 0030).
//!
//! A machine is not tied to one Mnemarr: it keeps a list of destinations, each with its own address, its own name for this machine, its own key and its own
//! positions. Every conversation is read once per pass and sent to each of them, so that a development instance and a production one both receive it, and
//! linking another does not touch the others. The list lives in the folder of the collector, in a file only this user reads, beside the keys.

use std::fs;
use std::io;
use std::path::Path;

use serde_json::{Value, json};

use crate::config::{BOOTSTRAP_FILE, KEY_FILE};
use crate::enrol::write_private;

/// The name of the file that lists the destinations, in the folder of the collector.
pub const FILE: &str = "destinations.json";

/// One Mnemarr that this machine sends to.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Destination {
    /// What a person calls it here: by default the host name of its address. Unique in the list.
    pub name: String,
    pub server: String,
    /// What this machine is called in *that* Mnemarr: each Mnemarr names its machines on its own.
    pub machine: String,
    /// Whether a plain `http://` address was accepted for it.
    pub insecure: bool,
    /// The file of its key, in the folder of the collector.
    pub key_file: String,
    /// The file of its positions, in the folder of the state.
    pub state_file: String,
}

/// A name for a destination from its address: the host name, lower case, in the characters a file name can have.
pub fn name_for(server: &str) -> String {
    let host = server
        .split("://")
        .last()
        .unwrap_or(server)
        .split(['/', '?', '#'])
        .next()
        .unwrap_or("");
    let host = host.rsplit_once('@').map_or(host, |(_, host)| host);
    let host = host
        .rsplit_once(':')
        .filter(|(_, port)| port.chars().all(|c| c.is_ascii_digit()))
        .map_or(host, |(name, _)| name);
    let cleaned: String = host
        .to_ascii_lowercase()
        .chars()
        .map(|c| {
            if c.is_ascii_alphanumeric() || c == '.' || c == '-' {
                c
            } else {
                '-'
            }
        })
        .take(40)
        .collect();
    let trimmed = cleaned.trim_matches(['-', '.']);
    if trimmed.is_empty() {
        "mnemarr".to_string()
    } else {
        trimmed.to_string()
    }
}

fn same_server(left: &str, right: &str) -> bool {
    left.trim_end_matches('/')
        .eq_ignore_ascii_case(right.trim_end_matches('/'))
}

fn to_json(destinations: &[Destination]) -> Value {
    json!({
        "destinations": destinations.iter().map(|destination| json!({
            "name": destination.name,
            "server": destination.server,
            "machine": destination.machine,
            "insecure": destination.insecure,
            "keyFile": destination.key_file,
            "stateFile": destination.state_file,
        })).collect::<Vec<_>>()
    })
}

fn from_json(document: &Value) -> Vec<Destination> {
    let text = |entry: &Value, name: &str| {
        entry
            .get(name)
            .and_then(Value::as_str)
            .filter(|value| !value.is_empty())
            .map(str::to_string)
    };
    document
        .get("destinations")
        .and_then(Value::as_array)
        .map(|entries| {
            entries
                .iter()
                .filter_map(|entry| {
                    Some(Destination {
                        name: text(entry, "name")?,
                        server: text(entry, "server")?,
                        machine: text(entry, "machine")?,
                        insecure: entry
                            .get("insecure")
                            .and_then(Value::as_bool)
                            .unwrap_or(false),
                        key_file: text(entry, "keyFile")?,
                        state_file: text(entry, "stateFile")?,
                    })
                })
                .collect()
        })
        .unwrap_or_default()
}

/// The destination that a collector of an earlier version had: one address, one name, one key, one file of positions. It becomes the first of the list
/// as it is, with its positions, so that nothing is sent twice.
fn legacy(dir: &Path) -> Option<Destination> {
    let text = fs::read_to_string(dir.join(BOOTSTRAP_FILE)).ok()?;
    let document: Value = serde_json::from_str(&text).ok()?;
    let field = |name: &str| {
        document
            .get(name)
            .and_then(Value::as_str)
            .filter(|value| !value.is_empty())
            .map(str::to_string)
    };
    if !dir.join(KEY_FILE).is_file() {
        return None;
    }
    let server = field("server")?;
    Some(Destination {
        name: name_for(&server),
        machine: field("machine")?,
        insecure: document
            .get("insecure")
            .and_then(Value::as_bool)
            .unwrap_or(false),
        server: server.trim_end_matches('/').to_string(),
        key_file: KEY_FILE.to_string(),
        state_file: "state.json".to_string(),
    })
}

/// The destinations kept in the folder of the collector. A folder of an earlier collector, with one address and one key, is read as a list of one (and the
/// list is written, so that the next time it is what it is).
pub fn load(dir: &Path) -> Vec<Destination> {
    if let Ok(text) = fs::read_to_string(dir.join(FILE)) {
        return serde_json::from_str::<Value>(&text)
            .map(|document| from_json(&document))
            .unwrap_or_default();
    }
    match legacy(dir) {
        Some(destination) => {
            let list = vec![destination];
            // Best effort: when it cannot be written, the next start reads the older files again.
            let _ = save(dir, &list);
            list
        }
        None => Vec::new(),
    }
}

/// Writes the list beside its place and moves it in, so that a crash leaves the old one.
pub fn save(dir: &Path, destinations: &[Destination]) -> io::Result<()> {
    fs::create_dir_all(dir)?;
    let text = serde_json::to_string_pretty(&to_json(destinations)).map_err(io::Error::other)?;
    let temporary = dir.join(format!("{FILE}.tmp"));
    fs::write(&temporary, text)?;
    fs::rename(&temporary, dir.join(FILE))
}

/// The key of a destination, if its file is there and not empty.
pub fn key_of(dir: &Path, destination: &Destination) -> Option<String> {
    fs::read_to_string(dir.join(&destination.key_file))
        .ok()
        .map(|text| text.trim().to_string())
        .filter(|text| !text.is_empty())
}

/// The list as it is, or an error when the file is there and cannot be read as a list: a change must never write over a list that only needs mending by hand.
pub fn load_for_change(dir: &Path) -> io::Result<Vec<Destination>> {
    match fs::read_to_string(dir.join(FILE)) {
        Ok(text) if serde_json::from_str::<Value>(&text).is_err() => Err(io::Error::new(
            io::ErrorKind::InvalidData,
            format!(
                "{FILE} cannot be read (is it edited by hand?): mend it or move it away, and nothing is written over it"
            ),
        )),
        _ => Ok(load(dir)),
    }
}

/// Links a destination: its key is kept where only this user reads it, and the list is written. A Mnemarr that is already in the list (the same address)
/// is replaced, keeping its name and its positions: it is the same destination with a new key. Another one with the same name gets a number.
pub fn add(
    dir: &Path,
    server: &str,
    machine: &str,
    insecure: bool,
    name: Option<&str>,
    key: &str,
) -> io::Result<Destination> {
    fs::create_dir_all(dir)?;
    let mut list = load_for_change(dir)?;
    let server = server.trim_end_matches('/').to_string();
    let chosen = if let Some(position) = list
        .iter()
        .position(|existing| same_server(&existing.server, &server))
    {
        let kept = list[position].clone();
        Destination {
            machine: machine.to_string(),
            insecure,
            server,
            ..kept
        }
    } else {
        let base = name.map_or_else(|| name_for(&server), name_for);
        let mut unique = base.clone();
        let mut number = 2;
        while list.iter().any(|existing| existing.name == unique) {
            unique = format!("{base}-{number}");
            number += 1;
        }
        Destination {
            key_file: format!("key-{unique}"),
            state_file: format!("state-{unique}.json"),
            name: unique,
            server,
            machine: machine.to_string(),
            insecure,
        }
    };
    write_private(&dir.join(&chosen.key_file), key)?;
    list.retain(|existing| existing.name != chosen.name);
    list.push(chosen.clone());
    save(dir, &list)?;
    Ok(chosen)
}

/// What this computer was called in that Mnemarr the last time it was linked to it (the address is the same), if it ever was. Linking again, it suggests that
/// name rather than its host name, so that it is the same machine again and not a second one.
pub fn machine_known_at(dir: &Path, server: &str) -> Option<String> {
    load(dir)
        .into_iter()
        .find(|existing| same_server(&existing.server, server))
        .map(|existing| existing.machine)
}

/// Takes a destination away, with its key. What it already received stays there. True when there was one of that name.
pub fn remove(dir: &Path, name: &str) -> io::Result<bool> {
    let mut list = load_for_change(dir)?;
    let Some(position) = list.iter().position(|existing| existing.name == name) else {
        return Ok(false);
    };
    let gone = list.remove(position);
    save(dir, &list)?;
    match fs::remove_file(dir.join(&gone.key_file)) {
        Err(error) if error.kind() != io::ErrorKind::NotFound => Err(error),
        _ => Ok(true),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn folder(name: &str) -> std::path::PathBuf {
        let dir = std::env::temp_dir().join(format!("mnemarr-dest-{name}-{}", std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        fs::create_dir_all(&dir).unwrap();
        dir
    }

    #[test]
    fn should_remember_what_this_computer_was_called_in_a_mnemarr_it_is_linked_to_again() {
        let dir = folder("known");
        assert_eq!(machine_known_at(&dir, "https://mnemarr.example"), None);
        add(
            &dir,
            "https://mnemarr.example",
            "gaming-pc",
            false,
            None,
            "key",
        )
        .unwrap();
        assert_eq!(
            machine_known_at(&dir, "https://mnemarr.example/").as_deref(),
            Some("gaming-pc")
        );
        assert_eq!(machine_known_at(&dir, "https://other.example"), None);
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn should_name_a_destination_after_the_host_of_its_address() {
        assert_eq!(name_for("https://mnemarr.example"), "mnemarr.example");
        assert_eq!(
            name_for("http://192.168.1.10:8384/some/path"),
            "192.168.1.10"
        );
        assert_eq!(
            name_for("https://User:pw@Host.Example:8443/"),
            "host.example"
        );
        assert_eq!(name_for("https://"), "mnemarr");
        assert_eq!(
            name_for("https://we ird_name.example"),
            "we-ird-name.example"
        );
        assert_eq!(name_for(&format!("https://{}", "a".repeat(80))).len(), 40);
    }

    #[test]
    fn should_link_two_instances_and_keep_each_with_its_own_name_for_this_machine_and_its_own_key()
    {
        let dir = folder("two");

        let prod = add(
            &dir,
            "https://prod.example/",
            "gaming-pc",
            false,
            None,
            "mnm_prod",
        )
        .unwrap();
        let dev = add(
            &dir,
            "http://dev.lan:8384",
            "gamng-pc",
            true,
            Some("dev"),
            "mnm_dev",
        )
        .unwrap();

        let list = load(&dir);
        assert_eq!(list, vec![prod.clone(), dev.clone()]);
        assert_eq!(prod.server, "https://prod.example", "no trailing slash");
        assert_eq!((dev.name.as_str(), dev.insecure), ("dev", true));
        assert_eq!(key_of(&dir, &prod).as_deref(), Some("mnm_prod"));
        assert_eq!(key_of(&dir, &dev).as_deref(), Some("mnm_dev"));
        assert_ne!(prod.state_file, dev.state_file, "positions are kept apart");
    }

    #[test]
    fn should_replace_the_key_and_the_name_of_an_instance_that_is_linked_again_and_keep_its_name_and_its_positions()
     {
        let dir = folder("again");
        let first = add(&dir, "https://prod.example", "pc", false, None, "old").unwrap();

        let second = add(
            &dir,
            "https://PROD.example/",
            "pc-renamed",
            false,
            Some("another"),
            "new",
        )
        .unwrap();

        assert_eq!(
            load(&dir).len(),
            1,
            "the same address is the same destination"
        );
        assert_eq!(
            (second.name.as_str(), second.state_file.as_str()),
            (first.name.as_str(), first.state_file.as_str())
        );
        assert_eq!(second.machine, "pc-renamed");
        assert_eq!(key_of(&dir, &second).as_deref(), Some("new"));
    }

    #[test]
    fn should_give_a_number_to_a_second_instance_that_would_have_the_same_name() {
        let dir = folder("same-name");
        add(&dir, "https://one.example", "pc", false, Some("work"), "k1").unwrap();

        let second = add(&dir, "https://two.example", "pc", false, Some("work"), "k2").unwrap();

        assert_eq!(second.name, "work-2");
        assert_eq!(load(&dir).len(), 2);
    }

    #[test]
    fn should_take_a_destination_away_with_its_key_and_say_when_there_was_none() {
        let dir = folder("remove");
        let prod = add(&dir, "https://prod.example", "pc", false, None, "k").unwrap();
        add(&dir, "https://dev.example", "pc", false, None, "k2").unwrap();

        assert!(remove(&dir, &prod.name).unwrap());

        assert_eq!(load(&dir).len(), 1);
        assert!(!dir.join(&prod.key_file).exists(), "its key is deleted");
        assert!(!remove(&dir, "nothing").unwrap());
    }

    #[test]
    fn should_read_the_folder_of_an_earlier_collector_as_one_destination_with_its_positions_and_write_the_list()
     {
        let dir = folder("legacy");
        fs::write(
            dir.join(BOOTSTRAP_FILE),
            r#"{"server":"http://192.168.1.10:8384","machine":"old-pc","insecure":true}"#,
        )
        .unwrap();
        fs::write(dir.join(KEY_FILE), "mnm_old\n").unwrap();

        let list = load(&dir);

        assert_eq!(list.len(), 1);
        assert_eq!(
            (
                list[0].machine.as_str(),
                list[0].insecure,
                list[0].key_file.as_str(),
                list[0].state_file.as_str()
            ),
            ("old-pc", true, "key", "state.json"),
            "the positions it had are the positions of this destination"
        );
        assert_eq!(key_of(&dir, &list[0]).as_deref(), Some("mnm_old"));
        assert!(dir.join(FILE).is_file(), "the list is written");
        assert_eq!(load(&dir), list);
    }

    #[test]
    fn should_have_no_destination_where_nothing_was_linked_or_the_file_is_not_a_list() {
        let dir = folder("none");
        assert!(load(&dir).is_empty());

        fs::write(dir.join(FILE), "not json").unwrap();
        assert!(load(&dir).is_empty());

        fs::write(dir.join(FILE), r#"{"destinations":[{"name":"x"}]}"#).unwrap();
        assert!(
            load(&dir).is_empty(),
            "an entry that lacks its address or its key is ignored"
        );
    }

    #[cfg(unix)]
    #[test]
    fn should_keep_each_key_where_only_its_user_reads_it() {
        use std::os::unix::fs::PermissionsExt;
        let dir = folder("private");

        let destination = add(&dir, "https://prod.example", "pc", false, None, "secret").unwrap();

        let mode = fs::metadata(dir.join(&destination.key_file))
            .unwrap()
            .permissions()
            .mode();
        assert_eq!(mode & 0o077, 0);
    }

    #[test]
    fn should_not_write_over_a_list_that_cannot_be_read() {
        let dir = std::env::temp_dir().join(format!("mnemarr-broken-{}", std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        fs::create_dir_all(&dir).unwrap();
        fs::write(dir.join(FILE), "{ this is not json").unwrap();

        let added = add(&dir, "https://m.example", "pc", false, None, "k");
        let removed = remove(&dir, "m.example");

        assert!(added.is_err() && removed.is_err());
        assert_eq!(
            fs::read_to_string(dir.join(FILE)).unwrap(),
            "{ this is not json"
        );
        let _ = fs::remove_dir_all(&dir);
    }
}
