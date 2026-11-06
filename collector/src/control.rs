//! The pause (ADR 0027 §16): a person can ask the collector to be quiet for a while, or until they say otherwise, from the icon or from the command
//! line. A file in the folder of the collector says so, so that what the icon writes, what `pause` writes and what the running collector reads are the
//! same thing, and a pause survives a restart: `paused` holds `forever`, or the moment it ends (seconds since 1970).

use std::fs;
use std::io;
use std::path::Path;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

/// The name of the file that says the collector is paused, in its folder.
pub const PAUSE_FILE: &str = "paused";
const FOREVER: &str = "forever";

/// How long a pause lasts.
#[derive(Debug, PartialEq, Eq, Clone, Copy)]
pub enum Pause {
    /// Until the person resumes it.
    Forever,
    /// Until this moment.
    Until(SystemTime),
}

fn seconds(moment: SystemTime) -> u64 {
    moment
        .duration_since(UNIX_EPOCH)
        .map_or(0, |since| since.as_secs())
}

/// Pauses the collector for a while.
pub fn pause_for(dir: &Path, length: Duration, now: SystemTime) -> io::Result<()> {
    fs::create_dir_all(dir)?;
    fs::write(dir.join(PAUSE_FILE), seconds(now + length).to_string())
}

/// Pauses the collector until it is resumed.
pub fn pause_forever(dir: &Path) -> io::Result<()> {
    fs::create_dir_all(dir)?;
    fs::write(dir.join(PAUSE_FILE), FOREVER)
}

/// Takes the pause away. Nothing happens when there is none.
pub fn resume(dir: &Path) -> io::Result<()> {
    match fs::remove_file(dir.join(PAUSE_FILE)) {
        Err(error) if error.kind() != io::ErrorKind::NotFound => Err(error),
        _ => Ok(()),
    }
}

/// The pause in force at this moment, if any. A pause whose time has passed is over: it is taken away, and a file that says nothing sensible is not one.
pub fn current(dir: &Path, now: SystemTime) -> Option<Pause> {
    let path = dir.join(PAUSE_FILE);
    let text = fs::read_to_string(&path).ok()?;
    let text = text.trim();
    if text == FOREVER {
        return Some(Pause::Forever);
    }
    let end = UNIX_EPOCH + Duration::from_secs(text.parse().ok()?);
    if end > now {
        Some(Pause::Until(end))
    } else {
        let _ = fs::remove_file(path);
        None
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn folder(name: &str) -> std::path::PathBuf {
        let dir =
            std::env::temp_dir().join(format!("mnemarr-control-{name}-{}", std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        dir
    }

    #[test]
    fn should_not_be_paused_when_nothing_was_asked() {
        let dir = folder("none");
        assert_eq!(current(&dir, SystemTime::now()), None);
        resume(&dir).unwrap();
    }

    #[test]
    fn should_pause_for_a_while_and_be_over_when_the_time_has_passed() {
        let dir = folder("while");
        let now = UNIX_EPOCH + Duration::from_secs(1_000_000);

        pause_for(&dir, Duration::from_secs(3600), now).unwrap();

        assert_eq!(
            current(&dir, now + Duration::from_secs(10)),
            Some(Pause::Until(now + Duration::from_secs(3600)))
        );
        assert_eq!(
            current(&dir, now + Duration::from_secs(3600)),
            None,
            "it ends at the moment it says"
        );
        assert!(
            !dir.join(PAUSE_FILE).exists(),
            "an ended pause is taken away"
        );
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn should_pause_until_resumed_and_survive_a_long_time() {
        let dir = folder("forever");

        pause_forever(&dir).unwrap();

        assert_eq!(
            current(&dir, UNIX_EPOCH + Duration::from_secs(4_000_000_000)),
            Some(Pause::Forever)
        );
        resume(&dir).unwrap();
        assert_eq!(current(&dir, SystemTime::now()), None);
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn should_not_take_for_a_pause_a_file_that_says_nothing_sensible() {
        let dir = folder("garbage");
        fs::create_dir_all(&dir).unwrap();
        fs::write(dir.join(PAUSE_FILE), "maybe later").unwrap();

        assert_eq!(current(&dir, SystemTime::now()), None);
        let _ = fs::remove_dir_all(&dir);
    }
}
