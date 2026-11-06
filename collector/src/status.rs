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

/// The condition, shared.
pub type Shared = Arc<Mutex<Condition>>;

pub fn shared() -> Shared {
    Arc::new(Mutex::new(Condition::Starting))
}

/// Says how the collector is.
pub fn set(shared: &Shared, condition: Condition) {
    if let Ok(mut current) = shared.lock() {
        *current = condition;
    }
}

/// How the collector is now.
pub fn get(shared: &Shared) -> Condition {
    shared
        .lock()
        .map_or(Condition::Starting, |current| current.clone())
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
}
