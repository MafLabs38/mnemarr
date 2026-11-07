//! The icon in the notification area (ADR 0027 §16): a person sees that the collector runs, how it is, and can pause it, resume it, open Mnemarr or stop
//! it, without a window and without a command. Windows only for now. The collector keeps collecting in its own thread; this one only draws and listens.

use std::convert::Infallible;
use std::path::PathBuf;
use std::sync::Arc;
use std::time::{Duration, Instant, SystemTime};

use tao::event_loop::{ControlFlow, EventLoop};
use tray_icon::menu::{Menu, MenuEvent, MenuItem, PredefinedMenuItem};
use tray_icon::{Icon, TrayIconBuilder};

use crate::browser;
use crate::config::default_machine;
use crate::control;
use crate::destinations;
use crate::link_page::{self, LinkPage};
use crate::status::{self, Condition, Shared};
use crate::update::{UpdateState, Updater};

/// The side of the icon, in pixels. Windows draws it smaller, from this.
const SIZE: u32 = 64;
/// How often the icon looks at how the collector is.
const REFRESH: Duration = Duration::from_millis(500);
/// How long "pause for a while" lasts.
const PAUSE: Duration = Duration::from_secs(3600);

// The brain of Mnemarr, as a thin line on a transparent ground; its colour says how the collector is. Drawn from `assets/brain.svg` by `tools/icons/render.mjs`.
const ONLINE: &[u8] = include_bytes!("../assets/brain-online.rgba");
const PAUSED: &[u8] = include_bytes!("../assets/brain-paused.rgba");
const OFFLINE: &[u8] = include_bytes!("../assets/brain-offline.rgba");
const STARTING: &[u8] = include_bytes!("../assets/brain-starting.rgba");

/// The icon for a condition: a green brain when all is well, orange when paused, red when Mnemarr cannot be reached or something is wrong, grey-blue while it starts.
fn icon_for(condition: &Condition) -> Result<Icon, String> {
    let pixels = match condition {
        Condition::Online => ONLINE,
        Condition::Paused => PAUSED,
        Condition::Starting => STARTING,
        Condition::Offline(_) | Condition::Problem(_) | Condition::Refused => OFFLINE,
    };
    Icon::from_rgba(pixels.to_vec(), SIZE, SIZE).map_err(|error| error.to_string())
}

/// The menu and what is in it that the loop must find again: the items it listens to and the lines it rewrites.
struct Items {
    menu: Menu,
    state_line: MenuItem,
    destination_lines: Vec<MenuItem>,
    pause_hour: MenuItem,
    pause_always: MenuItem,
    resume: MenuItem,
    /// One to open each Mnemarr, with the address it opens.
    open: Vec<(MenuItem, String)>,
    link: MenuItem,
    /// The way to look for a newer version, and to apply one that is ready: only for a collector that was installed with the installer.
    update: Option<MenuItem>,
    quit: MenuItem,
}

/// Builds the menu for the Mnemarr this machine sends to: a line of state, a line for each when there are several, the pause, one way to open each, and the
/// way to link another.
fn build(destinations: &[(String, String)], can_update: bool) -> Result<Items, String> {
    let menu = Menu::new();
    let title = MenuItem::new("Mnemarr collector", false, None);
    let state_line = MenuItem::new("starting", false, None);
    let destination_lines: Vec<MenuItem> = if destinations.len() > 1 {
        destinations
            .iter()
            .map(|(name, _)| MenuItem::new(format!("{name}: starting"), false, None))
            .collect()
    } else {
        Vec::new()
    };
    let pause_hour = MenuItem::new("Pause for one hour", true, None);
    let pause_always = MenuItem::new("Pause until I resume", true, None);
    let resume = MenuItem::new("Resume", false, None);
    let open: Vec<(MenuItem, String)> = destinations
        .iter()
        .map(|(name, server)| {
            let text = if destinations.len() > 1 {
                format!("Open {name}")
            } else {
                "Open Mnemarr".to_string()
            };
            (
                MenuItem::new(text, true, None),
                format!("{}/machines", server.trim_end_matches('/')),
            )
        })
        .collect();
    let link = MenuItem::new("Link to a Mnemarr...", true, None);
    let update = can_update.then(|| MenuItem::new("Check for updates", true, None));
    let quit = MenuItem::new("Quit the collector", true, None);
    let separator_one = PredefinedMenuItem::separator();
    let separator_two = PredefinedMenuItem::separator();
    let separator_three = PredefinedMenuItem::separator();
    menu.append(&title).map_err(|error| error.to_string())?;
    menu.append(&state_line)
        .map_err(|error| error.to_string())?;
    for line in &destination_lines {
        menu.append(line).map_err(|error| error.to_string())?;
    }
    menu.append(&separator_one)
        .map_err(|error| error.to_string())?;
    for item in [&pause_hour, &pause_always, &resume] {
        menu.append(item).map_err(|error| error.to_string())?;
    }
    menu.append(&separator_two)
        .map_err(|error| error.to_string())?;
    for (item, _) in &open {
        menu.append(item).map_err(|error| error.to_string())?;
    }
    menu.append(&link).map_err(|error| error.to_string())?;
    if let Some(item) = &update {
        menu.append(item).map_err(|error| error.to_string())?;
    }
    menu.append(&separator_three)
        .map_err(|error| error.to_string())?;
    menu.append(&quit).map_err(|error| error.to_string())?;
    Ok(Items {
        menu,
        state_line,
        destination_lines,
        pause_hour,
        pause_always,
        resume,
        open,
        link,
        update,
        quit,
    })
}

/// The Mnemarr this machine sends to, as the icon knows them: the list kept in the folder of the collector, or what it was started with.
fn current(dir: &std::path::Path, known: &[(String, String)]) -> Vec<(String, String)> {
    let listed = destinations::load(dir);
    if listed.is_empty() {
        known.to_vec()
    } else {
        listed
            .into_iter()
            .map(|destination| (destination.name, destination.server))
            .collect()
    }
}

/// Opens the page on this computer where a person links the collector to another Mnemarr.
fn open_link_page(dir: &std::path::Path) -> Option<LinkPage> {
    let get = |name: &str| std::env::var(name).ok();
    let read = |path: &str| std::fs::read_to_string(path).ok();
    let page = link_page::start(link_page::Environment {
        dir: dir.to_path_buf(),
        hostname: default_machine(&get, &read).unwrap_or_else(|| "this computer".to_string()),
        open: Arc::new(|address| {
            browser::open(address);
        }),
        wait: Arc::new(std::thread::sleep),
    })
    .ok()?;
    browser::open(&page.address);
    Some(page)
}

/// Shows the icon and listens to its menu. Does not return while the icon is there: *Quit* ends the program. It returns an error, before it shows
/// anything, when the icon cannot be made (no taskbar, a session with no desktop): the collector then simply goes on without it.
pub fn run(
    dir: PathBuf,
    known: Vec<(String, String)>,
    condition: Shared,
    updater: Updater,
) -> Result<Infallible, String> {
    let mut shown_destinations = current(&dir, &known);
    let mut items = build(&shown_destinations, updater.installed())?;
    let tray = TrayIconBuilder::new()
        .with_menu(Box::new(items.menu.clone()))
        .with_tooltip("Mnemarr collector")
        .with_icon(icon_for(&Condition::Starting)?)
        .build()
        .map_err(|error| error.to_string())?;

    let mut shown = Condition::Starting;
    let mut shown_lines: Vec<String> = Vec::new();
    let mut page: Option<LinkPage> = None;
    // The first time the collector runs and is linked to nothing, the page where a person links it opens by itself, once: nobody has to know where to look.
    let welcomed = dir.join("welcomed");
    if destinations::load(&dir).is_empty() && !welcomed.exists() {
        page = open_link_page(&dir);
        let _ = std::fs::write(&welcomed, "1");
    }
    let mut shown_update = String::new();
    EventLoop::new().run(move |_event, _target, control_flow| {
        *control_flow = ControlFlow::WaitUntil(Instant::now() + REFRESH);
        while let Ok(event) = MenuEvent::receiver().try_recv() {
            if event.id == *items.pause_hour.id() {
                let _ = control::pause_for(&dir, PAUSE, SystemTime::now());
            } else if event.id == *items.pause_always.id() {
                let _ = control::pause_forever(&dir);
            } else if event.id == *items.resume.id() {
                let _ = control::resume(&dir);
            } else if event.id == *items.link.id() {
                if let Some(previous) = page.take() {
                    previous.stop();
                }
                page = open_link_page(&dir);
            } else if items
                .update
                .as_ref()
                .is_some_and(|item| event.id == *item.id())
            {
                if updater.ready() {
                    let _ = updater.apply();
                } else {
                    let checker = updater.clone();
                    let listed = destinations::load(&dir);
                    std::thread::spawn(move || {
                        checker.check(&listed);
                    });
                }
            } else if event.id == *items.quit.id() {
                *control_flow = ControlFlow::Exit;
            } else if let Some((_, address)) =
                items.open.iter().find(|(item, _)| event.id == *item.id())
            {
                browser::open(address);
            }
        }
        // A Mnemarr that was linked from the page (or by a command) is in the menu from now on.
        let fresh = current(&dir, &known);
        if fresh != shown_destinations
            && let Ok(rebuilt) = build(&fresh, updater.installed())
        {
            tray.set_menu(Some(Box::new(rebuilt.menu.clone())));
            items = rebuilt;
            shown_destinations = fresh;
            shown_lines.clear();
            shown = Condition::Starting;
            shown_update.clear();
        }
        // What the update line says follows where the update is at.
        if let Some(item) = &items.update {
            let (text, enabled) = match updater.state() {
                UpdateState::Ready(version) => (format!("Update to {version}: restart now"), true),
                UpdateState::Checking | UpdateState::Downloading(_) => {
                    ("Looking for an update...".to_string(), false)
                }
                _ => ("Check for updates".to_string(), true),
            };
            if text != shown_update {
                item.set_text(&text);
                item.set_enabled(enabled);
                shown_update = text;
            }
        }
        // A pause is what the person asked for, and shows at once; the rest is what the collector last said of itself.
        let paused = control::current(&dir, SystemTime::now()).is_some();
        let now = if paused {
            Condition::Paused
        } else {
            status::get(&condition)
        };
        let lines = if paused {
            Vec::new()
        } else {
            status::lines(&condition)
        };
        if now != shown || lines != shown_lines {
            if let Ok(icon) = icon_for(&now) {
                let _ = tray.set_icon(Some(icon));
            }
            let mut tooltip = format!("Mnemarr collector: {}", now.label());
            for line in &lines {
                tooltip.push('\n');
                tooltip.push_str(line);
            }
            let _ = tray.set_tooltip(Some(tooltip));
            items.state_line.set_text(now.label());
            for (item, line) in items.destination_lines.iter().zip(&lines) {
                item.set_text(line);
            }
            items.resume.set_enabled(paused);
            items.pause_hour.set_enabled(!paused);
            items.pause_always.set_enabled(!paused);
            shown = now;
            shown_lines = lines;
        }
    })
}
