# Notifications

Octopushy tells you when something it watches goes wrong, when it recovers, and when a run waits for you. The bell at
the top of the sidebar lists the latest fifty notifications. One that arrives while the page is open also pops up at the
top right.

Nothing is configured for it.

## What is told

| Kind | When | Leads to |
| --- | --- | --- |
| `check-down` | an [uptime check](uptime.md) that answered up answers down | the node, machine or service |
| `check-up` | a check that answered down answers up | the same |
| `run-failed` | a run ends failed | the run |
| `run-uncertain` | a run's outcome becomes uncertain | the run |
| `run-approval` | a run stops for a person to approve its plan | the run |

A run that succeeds is not told, and neither is one whose plan its policy approves by itself. A run is told each time
it starts waiting, so one that is blocked while it waits, say because its installation must be trusted again, is told
again when it goes back to waiting.

A check's first answer tells nothing, since nothing changed. After that every turn is told, so a link that goes up and
down every minute is told every minute.

A run's notification names its operation and its resource, such as "Provision run failed" for `home/pve01/media`. It
never carries what the tool printed. That is on the run's page.

## In the app

The page asks for notifications every ten seconds.

- **The bell** counts what this browser has not read yet. Opening it lists the notifications, the unread ones marked,
  and closing it marks them read. Read is kept per browser, not per user.
- **Pop-ups** show at most three at once. If more arrive together, the rest are one line, "5 more notifications", that
  opens the list. A pop-up goes after eight seconds, waits while the pointer or the keyboard focus is on it, and has a
  button to dismiss it. A bar along its bottom edge shows the time it has left; the eight seconds start again when the
  pointer or the focus leaves. Clicking it opens the run or the resource.
- Nothing pops up for what was already there when the page loaded, or for what happened while the tab was away for more
  than a minute. The count on the bell covers those.

## How it is stored

`NotificationHub` adds one row to `notifications` in the same database save as the change it reports. A notification
exists exactly when the uptime answer or the run's status was stored. The hub sees a run turn through a save
interceptor, so it covers every place that fails a run and writes no run itself.

`GET /api/notifications` returns the latest fifty, newest first. Rows are never changed, and they are deleted after 31
days, like uptime answers. A row points at its run and resource by id without a foreign key, so a notification can
never make a run's save fail. Its link is dropped once the resource is gone.

## Providers

The app is the only place notifications go for now. Telegram, Discord and the like are not built.

A provider would read `notifications` in id order from a background loop, keeping the id it last sent. It must not send
from inside the save: a run's notification is added while the engine lock is held, and that lock cannot wait on someone
else's API. Still to decide then: where a provider's token lives, which kinds it sends, and what it does when sending
fails.
