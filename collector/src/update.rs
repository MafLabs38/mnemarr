//! Installing and updating through Velopack (ADR 0032).
//!
//! A collector that was installed with the installer of Mnemarr lives in its own folder (`%LocalAppData%\MnemarrCollector` on Windows), with a stable
//! program at the root that starts the current version, and can replace itself: it asks each Mnemarr it sends to for the newest version it carries, takes
//! the newest of them, downloads it in the background and applies it when it is quiet. A collector that was not installed that way (run by hand, from a
//! build, from a downloaded program that carries its setting) is not touched: `Updater::detect` says so, and nothing here does anything.
//!
//! What can be tried without Velopack is here as plain functions (which offer is the newest, which address may carry an update, what the settings say, what
//! the script that puts the program on the PATH says); what needs an installed program is a thin layer over the `velopack` crate.

use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};

use semver::Version;
use velopack::sources::HttpSource;
use velopack::{HttpOptions, UpdateCheck, UpdateInfo, UpdateManager, VelopackApp};

use crate::config::config_dir;
use crate::destinations::Destination;
#[cfg(windows)]
use crate::service;

/// The name of the package: the folder of the installed program is named after it.
pub const PACK_ID: &str = "MnemarrCollector";

/// The file in the folder of the collector that holds what the person chose about updates.
pub const SETTINGS_FILE: &str = "update.json";

/// How long a request to a Mnemarr for its feed or its package may take.
const TIMEOUT_MILLISECONDS: u64 = 60_000;

/// Where a Mnemarr serves its feed of updates (ADR 0032): `releases.<channel>.json` and the packages sit in this folder.
pub fn feed_url(server: &str) -> String {
    format!("{}/api/v1/ingest/updates", server.trim_end_matches('/'))
}

/// Whether a program may be taken from this address. An update is code that will run: it is taken over HTTPS, or from this very computer, and never over a plain
/// `http://` address on a network, even one that the person allowed for sending conversations.
pub fn may_carry_updates(server: &str) -> bool {
    let lower = server.trim().to_ascii_lowercase();
    if lower.starts_with("https://") {
        return true;
    }
    let Some(rest) = lower.strip_prefix("http://") else {
        return false;
    };
    let host = rest.split(['/', '?', '#']).next().unwrap_or_default();
    let host = host
        .strip_prefix('[')
        .and_then(|inner| inner.split_once(']'))
        .map_or_else(
            || host.split(':').next().unwrap_or_default(),
            |(inside, _)| inside,
        );
    matches!(host, "localhost" | "127.0.0.1" | "::1")
}

/// What one Mnemarr says is the newest version it carries (when it is newer than the one that is installed).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Offer {
    pub destination: String,
    pub version: Version,
}

/// The newest of the offers: the Mnemarr a collector takes its update from when it sends to several, which carry different builds (a development one and a
/// production one). When two carry the same version, the first one listed.
pub fn newest(offers: &[Offer]) -> Option<&Offer> {
    offers.iter().fold(None, |best, offer| match best {
        Some(found) if found.version >= offer.version => Some(found),
        _ => Some(offer),
    })
}

/// Where the collector is at, for the icon and for `status`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum UpdateState {
    /// Not installed with the installer: it updates by hand, and says nothing about it.
    NotInstalled,
    /// Installed, and nothing newer is known.
    UpToDate,
    /// Asking the Mnemarr it sends to.
    Checking,
    /// A newer version is being taken.
    Downloading(String),
    /// A newer version is here, and is applied when the collector restarts.
    Ready(String),
    /// The last attempt failed, with what went wrong.
    Failed(String),
}

impl UpdateState {
    /// A line for a person.
    pub fn text(&self) -> String {
        match self {
            Self::NotInstalled => {
                "not installed with the installer: it is not updated by itself".to_string()
            }
            Self::UpToDate => "up to date".to_string(),
            Self::Checking => "looking for an update".to_string(),
            Self::Downloading(version) => format!("taking version {version}"),
            Self::Ready(version) => {
                format!("version {version} is ready: it is applied when the collector restarts")
            }
            Self::Failed(reason) => format!("the last check failed: {reason}"),
        }
    }
}

/// What the person chose about updates.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Settings {
    /// An update that is ready is applied by the collector itself, between two passes. Off: it is applied when the person asks (the icon, `update`).
    pub auto: bool,
}

impl Default for Settings {
    fn default() -> Self {
        Self { auto: true }
    }
}

/// The settings of the folder, or the defaults when there are none or they cannot be read.
pub fn load_settings(dir: &Path) -> Settings {
    std::fs::read_to_string(dir.join(SETTINGS_FILE))
        .ok()
        .and_then(|text| serde_json::from_str::<serde_json::Value>(&text).ok())
        .and_then(|value| value.get("auto").and_then(serde_json::Value::as_bool))
        .map_or_else(Settings::default, |auto| Settings { auto })
}

/// Keeps the settings.
pub fn save_settings(dir: &Path, settings: &Settings) -> std::io::Result<()> {
    std::fs::create_dir_all(dir)?;
    let text = serde_json::json!({ "auto": settings.auto }).to_string();
    std::fs::write(dir.join(SETTINGS_FILE), text)
}

/// The program that starts the installed collector, at a place that an update does not change: the program that runs is in `current`, which is replaced, and
/// the one at the root of the folder starts it. A program that is not in an installed folder is its own.
pub fn stable_exe(running: &Path) -> PathBuf {
    let Some(file) = running.file_name() else {
        return running.to_path_buf();
    };
    let installed_root = running
        .parent()
        .filter(|folder| folder.file_name().is_some_and(|name| name == "current"))
        .and_then(Path::parent)
        .filter(|root| {
            root.join(if cfg!(windows) {
                "Update.exe"
            } else {
                "UpdateNix"
            })
            .exists()
        });
    installed_root.map_or_else(|| running.to_path_buf(), |root| root.join(file))
}

/// The script (PowerShell) that adds a folder to the PATH of the user, once, and tells Windows. Pure text, so that it is tried anywhere.
pub fn add_to_path_script(folder: &Path) -> String {
    let folder = folder.to_string_lossy().replace('\'', "''");
    format!(
        "$d = '{folder}'; $p = [Environment]::GetEnvironmentVariable('Path', 'User'); \
$parts = @(($p -split ';') | Where-Object {{ $_ -and ($_ -ne $d) }}); \
[Environment]::SetEnvironmentVariable('Path', (($parts + $d) -join ';'), 'User')"
    )
}

/// The script that takes a folder out of the PATH of the user.
pub fn remove_from_path_script(folder: &Path) -> String {
    let folder = folder.to_string_lossy().replace('\'', "''");
    format!(
        "$d = '{folder}'; $p = [Environment]::GetEnvironmentVariable('Path', 'User'); \
$parts = @(($p -split ';') | Where-Object {{ $_ -and ($_ -ne $d) }}); \
[Environment]::SetEnvironmentVariable('Path', ($parts -join ';'), 'User')"
    )
}

#[cfg(windows)]
fn run_script(script: &str) {
    use std::os::windows::process::CommandExt;
    const CREATE_NO_WINDOW: u32 = 0x0800_0000;
    let root = std::env::var_os("SystemRoot").unwrap_or_else(|| "C:\\Windows".into());
    let _ = std::process::Command::new(
        Path::new(&root)
            .join("System32")
            .join("WindowsPowerShell")
            .join("v1.0")
            .join("powershell.exe"),
    )
    .args([
        "-NoProfile",
        "-NonInteractive",
        "-ExecutionPolicy",
        "Bypass",
        "-Command",
        script,
    ])
    .creation_flags(CREATE_NO_WINDOW)
    .output();
}

/// What the installer asks of the program, in the thirty seconds it gives it (Windows): the program is made to start with the session of the user and to be
/// found by name in a terminal. It is not started here: the installer starts it when it is done.
#[cfg(windows)]
fn after_install() {
    let Ok(running) = std::env::current_exe() else {
        return;
    };
    let stable = stable_exe(&running);
    let _ = service::register(&stable, None);
    if let Some(root) = stable.parent() {
        run_script(&add_to_path_script(root));
    }
}

/// Before the program is taken away: it stops, and no longer starts with the session or sits on the PATH. The machine stays joined: its keys and its
/// positions are in the folder of the collector, which is not the one that is removed (ADR 0032).
#[cfg(windows)]
fn before_uninstall() {
    let get = |name: &str| std::env::var(name).ok();
    if let Some(dir) = config_dir(None, &get) {
        let _ = service::uninstall(&dir);
    }
    if let Ok(running) = std::env::current_exe()
        && let Some(root) = stable_exe(&running).parent()
    {
        run_script(&remove_from_path_script(root));
    }
}

/// Before a new version replaces this one: the running collector stops, so that its files can be replaced. The updater starts the new one.
#[cfg(windows)]
fn before_update() {
    let get = |name: &str| std::env::var(name).ok();
    if let Some(dir) = config_dir(None, &get) {
        service::stop(&dir);
    }
}

/// The first thing `main` does. When the program is started by the installer or the updater to do one of its tasks, this does it and ends the process; when
/// a new version was downloaded and waits, it is applied here; otherwise it returns and the program goes on as it always did.
pub fn startup() {
    let app = VelopackApp::build();
    #[cfg(windows)]
    let app = app
        .on_after_install_fast_callback(|_| after_install())
        .on_before_uninstall_fast_callback(|_| before_uninstall())
        .on_before_update_fast_callback(|_| before_update());
    let mut app = app;
    app.run();
}

/// An address at which this program may ask for updates, with the name of the destination it belongs to.
fn source_of(server: &str) -> HttpSource {
    HttpSource::new_with_options(
        feed_url(server),
        HttpOptions {
            Headers: Vec::new(),
            TimeoutMilliseconds: TIMEOUT_MILLISECONDS,
        },
    )
}

/// What was found by a check, and kept until it is applied.
struct Pending {
    manager: UpdateManager,
    info: UpdateInfo,
}

/// The collector's side of updates: whether it is installed, where it is at, and the update that is ready.
#[derive(Clone)]
pub struct Updater {
    version: Option<Version>,
    state: Arc<Mutex<UpdateState>>,
    pending: Arc<Mutex<Option<Arc<Pending>>>>,
}

impl Updater {
    /// Finds out whether this program was installed with the installer (and which version it is).
    pub fn detect() -> Self {
        let version = UpdateManager::new(HttpSource::new(""), None, None)
            .ok()
            .map(|manager| manager.get_current_version());
        let state = if version.is_some() {
            UpdateState::UpToDate
        } else {
            UpdateState::NotInstalled
        };
        Self {
            version,
            state: Arc::new(Mutex::new(state)),
            pending: Arc::new(Mutex::new(None)),
        }
    }

    /// Whether the program was installed with the installer, and can replace itself.
    pub fn installed(&self) -> bool {
        self.version.is_some()
    }

    /// The version that is installed, as the installer knows it.
    pub fn version(&self) -> Option<String> {
        self.version.as_ref().map(ToString::to_string)
    }

    pub fn state(&self) -> UpdateState {
        self.state
            .lock()
            .map_or(UpdateState::NotInstalled, |state| state.clone())
    }

    fn set(&self, state: UpdateState) {
        if let Ok(mut current) = self.state.lock() {
            *current = state;
        }
    }

    /// Whether an update is downloaded and waits to be applied.
    pub fn ready(&self) -> bool {
        matches!(self.state(), UpdateState::Ready(_))
    }

    /// Asks each Mnemarr in the list what it carries, takes the newest version of those that are newer than the installed one, and downloads it. Blocks for as
    /// long as that takes: it is run apart from the passes. A Mnemarr that cannot be reached or whose address may not carry updates is passed over.
    pub fn check(&self, destinations: &[Destination]) -> UpdateState {
        if !self.installed() {
            return UpdateState::NotInstalled;
        }
        if matches!(
            self.state(),
            UpdateState::Checking | UpdateState::Downloading(_)
        ) {
            return self.state();
        }
        self.set(UpdateState::Checking);
        let mut offers = Vec::new();
        let mut candidates: Vec<(Offer, Pending)> = Vec::new();
        let mut last_failure = None;
        for destination in destinations
            .iter()
            .filter(|destination| may_carry_updates(&destination.server))
        {
            let manager = match UpdateManager::new(source_of(&destination.server), None, None) {
                Ok(manager) => manager,
                Err(error) => {
                    last_failure = Some(error.to_string());
                    continue;
                }
            };
            match manager.check_for_updates() {
                Ok(UpdateCheck::UpdateAvailable(info)) => {
                    let Ok(version) = Version::parse(&info.TargetFullRelease.Version) else {
                        continue;
                    };
                    let offer = Offer {
                        destination: destination.name.clone(),
                        version,
                    };
                    offers.push(offer.clone());
                    candidates.push((
                        offer,
                        Pending {
                            manager,
                            info: *info,
                        },
                    ));
                }
                Ok(_) => {}
                Err(error) => last_failure = Some(format!("{}: {error}", destination.name)),
            }
        }
        let Some(best) = newest(&offers).cloned() else {
            let state = last_failure.map_or(UpdateState::UpToDate, UpdateState::Failed);
            self.set(state.clone());
            return state;
        };
        let Some(position) = candidates.iter().position(|(offer, _)| *offer == best) else {
            self.set(UpdateState::UpToDate);
            return UpdateState::UpToDate;
        };
        let (_, pending) = candidates.swap_remove(position);
        self.set(UpdateState::Downloading(best.version.to_string()));
        if let Err(error) = pending.manager.download_updates(&pending.info, None) {
            let state = UpdateState::Failed(format!("{}: {error}", best.destination));
            self.set(state.clone());
            return state;
        }
        if let Ok(mut slot) = self.pending.lock() {
            *slot = Some(Arc::new(pending));
        }
        let state = UpdateState::Ready(best.version.to_string());
        self.set(state.clone());
        state
    }

    /// Applies the update that is ready, and restarts the collector: the process ends and the updater starts the new version. Positions are kept after each
    /// pass, so nothing is lost wherever this is called from. An error when there is none ready, or when the updater could not be started.
    pub fn apply(&self) -> Result<(), String> {
        let pending = self
            .pending
            .lock()
            .map_err(|_| "the update cannot be read".to_string())?
            .clone()
            .ok_or_else(|| "no update is ready".to_string())?;
        pending
            .manager
            .apply_updates_and_restart(&pending.info)
            .map_err(|error| error.to_string())
    }
}

/// Where the settings and the keys are kept, for the commands that have no `--config-dir` of their own.
pub fn default_config_dir() -> Option<PathBuf> {
    let get = |name: &str| std::env::var(name).ok();
    config_dir(None, &get)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn offer(destination: &str, version: &str) -> Offer {
        Offer {
            destination: destination.to_string(),
            version: Version::parse(version).unwrap(),
        }
    }

    #[test]
    fn should_take_the_update_from_the_mnemarr_that_carries_the_newest_version() {
        let offers = [
            offer("dev", "0.3.0-dev.12"),
            offer("prod", "0.2.0"),
            offer("other", "0.3.0-dev.9"),
        ];

        assert_eq!(newest(&offers).unwrap().destination, "dev");
        assert!(newest(&[]).is_none());
    }

    #[test]
    fn should_order_a_release_after_its_pre_releases_and_numbers_after_numbers() {
        let offers = [
            offer("a", "1.0.0-dev.9"),
            offer("b", "1.0.0"),
            offer("c", "1.0.0-dev.10"),
        ];

        assert_eq!(newest(&offers).unwrap().destination, "b");
        assert_eq!(
            newest(&[offer("a", "1.0.0-dev.9"), offer("c", "1.0.0-dev.10")])
                .unwrap()
                .destination,
            "c",
            "dev.10 is newer than dev.9: numbers are compared as numbers"
        );
    }

    #[test]
    fn should_take_the_first_listed_when_two_carry_the_same_version() {
        let offers = [offer("first", "1.2.3"), offer("second", "1.2.3")];

        assert_eq!(newest(&offers).unwrap().destination, "first");
    }

    #[test]
    fn should_build_the_address_of_the_feed_of_a_mnemarr() {
        assert_eq!(
            feed_url("https://mnemarr.example/"),
            "https://mnemarr.example/api/v1/ingest/updates"
        );
        assert_eq!(
            feed_url("http://127.0.0.1:8383"),
            "http://127.0.0.1:8383/api/v1/ingest/updates"
        );
    }

    #[test]
    fn should_take_a_program_only_over_https_or_from_this_computer() {
        assert!(may_carry_updates("https://mnemarr.example"));
        assert!(may_carry_updates("HTTPS://Mnemarr.Example/path"));
        assert!(may_carry_updates("http://localhost:8383"));
        assert!(may_carry_updates("http://127.0.0.1:8383/"));
        assert!(may_carry_updates("http://[::1]:8383"));
        assert!(!may_carry_updates("http://10.0.0.10:8384"));
        assert!(!may_carry_updates("http://mnemarr.example"));
        assert!(!may_carry_updates("http://localhost.evil.example"));
        assert!(!may_carry_updates("ftp://mnemarr.example"));
        assert!(!may_carry_updates("mnemarr.example"));
    }

    #[test]
    fn should_say_in_words_where_an_update_is_at() {
        assert!(UpdateState::NotInstalled.text().contains("not installed"));
        assert!(
            UpdateState::Ready("1.2.3".to_string())
                .text()
                .contains("1.2.3")
        );
        assert!(
            UpdateState::Failed("down".to_string())
                .text()
                .contains("down")
        );
    }

    #[test]
    fn should_update_by_itself_unless_the_person_said_otherwise_and_keep_what_they_said() {
        let dir = std::env::temp_dir().join(format!("mnemarr-update-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);

        assert!(load_settings(&dir).auto, "no file: the default");
        save_settings(&dir, &Settings { auto: false }).unwrap();
        assert!(!load_settings(&dir).auto);
        std::fs::write(dir.join(SETTINGS_FILE), "{ not json").unwrap();
        assert!(
            load_settings(&dir).auto,
            "a file that cannot be read: the default"
        );
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn should_find_the_program_at_the_root_of_an_installed_folder_and_leave_any_other_alone() {
        let root = std::env::temp_dir().join(format!("mnemarr-root-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&root);
        std::fs::create_dir_all(root.join("current")).unwrap();
        let updater = root.join(if cfg!(windows) {
            "Update.exe"
        } else {
            "UpdateNix"
        });
        std::fs::write(&updater, "x").unwrap();

        let running = root.join("current").join("mnemarr-collector.exe");
        assert_eq!(stable_exe(&running), root.join("mnemarr-collector.exe"));

        let elsewhere = std::env::temp_dir()
            .join("current")
            .join("mnemarr-collector.exe");
        assert_eq!(
            stable_exe(&elsewhere),
            elsewhere,
            "no updater beside it: it is not installed"
        );
        assert_eq!(
            stable_exe(Path::new("mnemarr-collector")),
            PathBuf::from("mnemarr-collector")
        );
        let _ = std::fs::remove_dir_all(&root);
    }

    #[test]
    fn should_write_scripts_that_add_a_folder_to_the_path_once_and_take_it_away() {
        let add = add_to_path_script(Path::new(r"C:\Users\me\AppData\Local\MnemarrCollector"));
        let remove =
            remove_from_path_script(Path::new(r"C:\Users\me\AppData\Local\MnemarrCollector"));

        assert!(add.contains(r"$d = 'C:\Users\me\AppData\Local\MnemarrCollector'"));
        assert!(
            add.contains("'Path', 'User'"),
            "the PATH of the user, never the machine's"
        );
        assert!(
            add.contains("-ne $d") && add.contains("($parts + $d)"),
            "once: it is taken out before it is added"
        );
        assert!(remove.contains("($parts -join ';')") && !remove.contains("+ $d"));
        assert!(
            add_to_path_script(Path::new("C:\\it's")).contains("'C:\\it''s'"),
            "a quote in the folder cannot end the string"
        );
    }
}
