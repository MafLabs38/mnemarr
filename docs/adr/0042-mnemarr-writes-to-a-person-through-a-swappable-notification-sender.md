# 0042 — Mnemarr writes to a person through a swappable notification sender

- **Status**: accepted
- **Date**: 2026-11-12
- **Spec**: D-63; completes D-62 ([0041](0041-a-satellite-sends-a-neutral-format-and-can-ask-a-person-for-attention.md))

## Context

A satellite that reads a web assistant as a signed-in user loses its session now and then, and says so with the `attention` signal (ADR 0041). The machines page shows it, but the person is not on that page: the satellite
runs unattended and the conversations stop until somebody signs in again. Something has to reach them. The operator's mailbox is a Google account, and Google offers SMTP with an application password, so a plain SMTP
adapter is enough; but Mnemarr is a generic product and the channel must be replaceable (another mail service, a chat, a push) without touching the policy of when to write.

## Decision

1. **A small notifications feature, not one for a single satellite.** `INotificationSender` takes a `Notification` (recipient, subject, text) and returns a result, delivered or a short reason; it never throws for a delivery that
   fails. The channel is chosen by name, `Notifications:Provider`, the way the title providers are (ADR 0028): an `INotificationProvider` says what settings it needs, checks them, and builds the sender. **One
   provider comes first, `Smtp`**, built on the framework's own client (no dependency added); another channel is one class and one registration line, documented in `docs/configuration.md`.
2. **Off by default, an explicit recipient.** Nothing is ever sent to an address a machine, a note or a conversation gave: only to `Notifications:Recipient`. The password is a file, read at each send (so it can be
   rotated), never an environment variable. STARTTLS on port 587 is the connection (the implicit TLS of port 465 is not supported); it can be turned off only without a password, for a catcher on a test machine.
3. **The policy is Mnemarr's.** For the attention signal: **one mail when an episode begins**, then a reminder **at most once a day** (`ReminderHours`) and at most `MaxReminders` of them while it lasts, **nothing
   when it ends**. What was sent is stored with the machine (ADR 0041), so a restart sends nothing twice. A mail that fails is recorded, not marked as sent, and tried again after `RetryMinutes`, never in a tight loop.
4. **Caps that hold whatever a machine does.** Every attempt, sent or not, goes in a small log (`notification_log`): at most `MaxPerMachinePerDay` for one machine and `MaxPerDay` for all of them in the last
   24 hours. A satellite key that starts and stops asking in a loop (a new episode each time) cannot become a mail cannon, and a mail server that is down is not hammered. Only a machine that is enrolled and not
   revoked is ever mailed.
5. **The text is Mnemarr's own.** The subject and the body are made from the machine's name (a restricted charset) and the sentence of the closed code. **Not even the address the satellite gave is written
   in the mail**: a mail comes from the operator's own mailbox, and an ingest key that leaked could make Mnemarr send a convincing "sign in here" to the person. The mail points to the Machines page of
   this Mnemarr (`Collectors:PublicUrl` + `/machines`, when it is set), where the person is signed in and the satellite's link is shown with its host. No token, no key, no conversation text, no path, and
   nothing free that the satellite wrote.
6. **It is visible.** The machine's line says how many mails were sent and, when the last one did not leave, why (a sentence of Mnemarr, never the server's text). A card on the machines page, for an administrator,
   says where mails go and lets them send a test mail (`POST /api/v1/notifications/test`, `collectors.manage`, counted in the daily cap). The readiness probe `notifications` is degraded, never unhealthy, with the
   reason `notifications-secret` (the password file cannot be read) or `notifications-failing` (the last mail did not leave); off, it is always healthy.

## Consequences

- A satellite needs no code of its own to reach a person; Mnemarr stays ignorant of Google. Adding Gmail's API, a chat message or a push later is one class that implements `INotificationSender`.
- The framework's `SmtpClient` is documented as not recommended for new code and has no implicit TLS; for one rare mail to one person on port 587 that is acceptable, and replacing it with MailKit, if ever needed,
  is a dependency decision for the maintainer, behind the same interface.
- One table, `notification_log`, pruned after seven days. A new Sonar exception is documented in the code (S5332 at the one place STARTTLS can be turned off, which the validation refuses with a password).
