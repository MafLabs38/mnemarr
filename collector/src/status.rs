//! What the collector is doing, as a person sees it on the icon: shared between the loop that collects and the icon that shows it.

use std::sync::{Arc, Mutex};

/// How the collector is, after its last pass.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Condition {
    /// It has not finished a pass yet.
    Starting,
    /// It reached Mnemarr, and all is well.
    Online,
    /// A person asked it to be quiet.
    Paused,
    /// Mnemarr could not be reached: nothing is lost, a later pass sends it.
    Offline(String),
    /// A file could not be read, or a conversation was refused.
    Problem(String),
    /// The key was refused: the machine was revoked, or has to join again.
    Refused,
}

impl Condition {
    /// A few words for the menu and the tooltip.
    pub fn label(&self) -> String {
        match self {
            Self::Starting => "starting".to_string(),
            Self::Online => "online".to_string(),
            Self::Paused => "paused".to_string(),
            Self::Offline(why) => format!("cannot reach Mnemarr ({why})"),
            Self::Problem(what) => format!("problem: {what}"),
            Self::Refused => "refused: this machine was revoked, join it again".to_string(),
        }
    }
}

/// How bad a condition is, to tell which of several is the one to show.
fn severity(condition: &Condition) -> u8 {
    match condition {
        Condition::Refused => 5,
        Condition::Offline(_) => 4,
        Condition::Problem(_) => 3,
        Condition::Starting => 2,
        Condition::Online => 1,
        Condition::Paused => 0,
    }
}

/// The condition to show for several destinations: the worst of them, so that the icon is green only when all is well. A pause is for all of them.
pub fn worst(conditions: &[Condition]) -> Condition {
    conditions
        .iter()
        .max_by_key(|condition| severity(condition))
        .cloned()
        .unwrap_or(Condition::Starting)
}

/// What is shared: the condition to show, and how each destination is when there are several.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Snapshot {
    pub condition: Condition,
    pub destinations: Vec<(String, Condition)>,
}

/// The condition, shared.
pub type Shared = Arc<Mutex<Snapshot>>;

pub fn shared() -> Shared {
    Arc::new(Mutex::new(Snapshot {
        condition: Condition::Starting,
        destinations: Vec::new(),
    }))
}

/// Says how the collector is.
pub fn set(shared: &Shared, condition: Condition) {
    if let Ok(mut current) = shared.lock() {
        current.condition = condition;
        current.destinations.clear();
    }
}

/// Says how the collector is for each destination: the one to show is the worst, or the pause when a person asked for one.
pub fn set_all(shared: &Shared, destinations: Vec<(String, Condition)>, paused: bool) {
    if let Ok(mut current) = shared.lock() {
        current.condition = if paused {
            Condition::Paused
        } else {
            worst(
                &destinations
                    .iter()
                    .map(|(_, condition)| condition.clone())
                    .collect::<Vec<_>>(),
            )
        };
        current.destinations = destinations;
    }
}

/// How the collector is now.
pub fn get(shared: &Shared) -> Condition {
    shared
        .lock()
        .map_or(Condition::Starting, |current| current.condition.clone())
}

/// One line for each destination when there are several ("prod.example: online"), for the menu and the tooltip; none for a single one.
pub fn lines(shared: &Shared) -> Vec<String> {
    shared.lock().map_or_else(
        |_| Vec::new(),
        |current| {
            if current.destinations.len() < 2 {
                return Vec::new();
            }
            current
                .destinations
                .iter()
                .map(|(name, condition)| format!("{name}: {}", condition.label()))
                .collect()
        },
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn should_say_in_words_how_the_collector_is() {
        assert_eq!(Condition::Online.label(), "online");
        assert_eq!(Condition::Paused.label(), "paused");
        assert!(
            Condition::Offline("no route".to_string())
                .label()
                .contains("no route")
        );
        assert!(
            Condition::Problem("2 files".to_string())
                .label()
                .starts_with("problem")
        );
        assert!(Condition::Refused.label().contains("revoked"));
    }

    #[test]
    fn should_share_the_condition_between_the_loop_and_the_icon() {
        let shared = shared();
        assert_eq!(get(&shared), Condition::Starting);

        set(&shared, Condition::Paused);

        assert_eq!(get(&shared), Condition::Paused);
    }

    #[test]
    fn should_show_the_worst_condition_of_several_destinations_so_that_green_means_all_is_well() {
        let shared = shared();

        set_all(
            &shared,
            vec![
                ("prod".to_string(), Condition::Online),
                (
                    "dev".to_string(),
                    Condition::Offline("no route".to_string()),
                ),
            ],
            false,
        );

        assert!(matches!(get(&shared), Condition::Offline(_)));
        assert_eq!(
            lines(&shared),
            vec![
                "prod: online".to_string(),
                "dev: cannot reach Mnemarr (no route)".to_string()
            ]
        );
        set_all(
            &shared,
            vec![
                ("prod".to_string(), Condition::Online),
                ("dev".to_string(), Condition::Online),
            ],
            false,
        );
        assert_eq!(get(&shared), Condition::Online);
        set_all(&shared, vec![("prod".to_string(), Condition::Online)], true);
        assert_eq!(
            get(&shared),
            Condition::Paused,
            "a pause is for all of them"
        );
        assert!(
            lines(&shared).is_empty(),
            "one destination needs no line of its own"
        );
        assert_eq!(worst(&[]), Condition::Starting);
        assert_eq!(
            worst(&[
                Condition::Online,
                Condition::Refused,
                Condition::Problem("x".to_string())
            ]),
            Condition::Refused
        );
    }
}
