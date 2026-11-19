# Notifications

Octopushy tells you when something it watches goes wrong, when it recovers, and when a run waits for you. The bell at
the top of the sidebar lists the latest fifty notifications, and the Notifications page lists all that are kept. One that
arrives while the page is open also pops up at the top right. A [webhook](#the-webhook) can also post them to one URL, so you find out without opening the app.

The bell needs no setup.

## What is told

| Kind | When | Leads to |
| --- | --- | --- |
| `check-down` | an [uptime check](uptime.md) that answered up answers down | the node, machine or service |
| `check-up` | a check that answered down answers up | the same |
| `run-failed` | a run ends failed | the run |
| `run-uncertain` | a run's outcome becomes uncertain | the run |
| `run-approval` | a run stops for a person to approve its plan | the run |
| `agent-offline` | an enabled agent has been away for five minutes | the Agents page |
| `agent-online` | an agent told offline is connected again | the Agents page |
| `agent-update-failed` | an agent's [update](worker-routing.md) failed | the Agents page |

A run that succeeds is not told, and neither is one whose plan its policy approves by itself. A run is told each time
it starts waiting, so one that is blocked while it waits, say because its installation must be trusted again, is told
again when it goes back to waiting.

A check's first answer tells nothing, since nothing changed. After that every turn is told, so a link that goes up and
down every minute is told every minute.

An agent is offline once it is enabled, has connected at least once, is not connected now, and was last seen more than
five minutes ago, counted from when the server started at the earliest. The server checks once a minute, so it is told
five to six minutes after the agent went. An agent that comes back within five minutes, as one does when it or the
server restarts or the network blips, is never told. A disabled agent is never told offline. One told offline is told
online once when it connects again, and offline again only after that. Why an update failed is on the Agents page, not in
the notification.

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
- **The Notifications page**, at `/notifications`, lists every notification kept, the last 31 days, newest first and
  grouped by day. The bell's panel links to it. Filters keep runs, uptime or agents, only the unread, or those whose
  title or text holds what you type. Notifications in a row about one check, two downs or more among them, are folded
  into one line. **Mark all read** marks everything read in this browser. **Load older** fetches the next page.
- Nothing pops up for what was already there when the page loaded, or for what happened while the tab was away for more
  than a minute. The count on the bell covers those.

## How it is stored

`NotificationHub` adds one row to `notifications` in the same database save as the change it reports. A notification
exists exactly when the uptime answer, the run's status, or the agent's state was stored. The hub sees a run turn through a
save interceptor, so it covers every place that fails a run and writes no run itself. An agent's notifications are added
by the code that saves its state: the presence check, which writes only the agent's `offline_notified_at`, and the
update that fails.

`GET /api/notifications` returns a page of notifications, newest first: with no parameters, the latest fifty. `limit`
sets how many, from 1 to 200. `before` and `after` keep ids below or above one. `kind`, repeated for more than one, keeps
those kinds. `q`, at most 200 characters, keeps those whose title or body holds the text, ignoring case; `%` and `_` in it match only
themselves. Anything out of these bounds, or an unknown kind, is a 400. The answer's `nextBefore` is the `before` for the
next page, or null on the last. Each item also carries its run's number, its resource's address and its agent's name,
each null once that row is gone. Rows are never changed, and they are deleted after 31
days, like uptime answers. A row points at its run, resource or agent by id without a foreign key, so a notification can
never make a run's save fail. Its link is dropped once the resource is gone.

## The webhook

Set it at `/notifications/webhook`, which the Notifications page links to: a URL, and the kinds to send. By default those are `run-approval`,
`run-failed`, `run-uncertain`, `agent-offline`, `agent-online` and `agent-update-failed`. The uptime kinds are left
out, because a check that flaps would post every minute; tick them to have them too. **Send a test event** posts a
`test` event to the URL at once, past the queue, and says what the receiver answered. It may be pressed six times a
minute.

The URL is an absolute `https://` URL of at most 2000 characters, with no user name, password or `#fragment`. Put a
token in its path or query if the receiver needs one. `Octopushy:Notifications:AllowHttpWebhook` lets it be
`http://`, for a receiver on the local network without TLS; the events and their signature then cross the network in the
clear. The URL is kept as a secret in the variable store, under `system/webhook/url`, which the Variables page and its
API never show or reveal, and no definition may name. Once saved, the page shows only its host, and nothing logs it.

Saving a URL also makes a new signing secret, kept under `system/webhook/signing-secret` and shown once. The first URL,
and one saved after sending stopped, start from then on: nothing told before is sent, so the last 31 days are not
replayed. A new URL for a webhook that is still sending keeps what waits, which then goes to the new URL. **Remove**
deletes the URL and the secret.

### What is sent

One POST per notification, with a JSON body of at most a few hundred bytes:

```json
{
  "id": "1042",
  "kind": "run-failed",
  "test": false,
  "at": "2026-11-17T09:12:03.0000000+00:00",
  "title": "Provision run failed",
  "run": { "id": "0193…", "number": 57 },
  "resource": { "id": "0192…", "kind": "guest", "address": "home/pve01/media" },
  "agent": null,
  "url": "https://octopushy.example.org/runs/0193…"
}
```

| Field | What |
| --- | --- |
| `id` | The notification's id, the same each time the event is tried again: a receiver drops one it has seen. A test event's is `test-` and 32 hex digits. |
| `kind` | One of the kinds above, or `test`. |
| `test` | True for the test event only. |
| `at` | When it happened, UTC. |
| `title` | The notification's title, written by the server, at most 200 characters. |
| `run` | The run's id and number, or null. |
| `resource` | The resource's id, kind and address (at most 300 characters), or null. Null once the resource is gone. |
| `agent` | The agent's id and name (at most 100 characters), or null. |
| `url` | The page it leads to, under `Octopushy:PublicUrl`. Null when that setting is not set: the server never takes its address from a request. |

A notification's body, what a tool printed, an error, a plan, a variable's name or value never go out. Addresses and
names have control and formatting characters taken out.

Headers: `Content-Type: application/json; charset=utf-8`, `X-Octopushy-Event` (the id), `X-Octopushy-Timestamp` (Unix
seconds when it was sent), and `X-Octopushy-Signature`.

### Checking the signature

`X-Octopushy-Signature` is `sha256=` and the lowercase hex HMAC-SHA256, keyed with the signing secret's UTF-8 bytes
(the whole `whsec_…` string), of the timestamp header's value, a `.`, and the body's bytes exactly as received. A
receiver computes it, compares it in constant time, and refuses a timestamp more than five minutes from its own clock,
so a captured event cannot be replayed later:

```python
import hashlib, hmac, time

def genuine(secret: str, timestamp: str, body: bytes, signature: str) -> bool:
    expected = "sha256=" + hmac.new(secret.encode(), timestamp.encode() + b"." + body, hashlib.sha256).hexdigest()
    return hmac.compare_digest(expected, signature) and abs(time.time() - int(timestamp)) < 300
```

Without the secret, someone who learns the receiver's URL can post to it but cannot sign what they post.

### Delivery

A background loop sends the notifications of the chosen kinds one at a time, in id order, from the id it last finished.
It runs only in the server process that owns the database, never in `migrate`. It never takes the engine's lock and never
sends from inside a save. A save that adds a notification wakes it. Otherwise it looks once a minute.

- A 2xx answer is a delivery. No redirect is followed, so a 3xx is a failure.
- A 410 Gone stops sending. The webhook page says so until the URL is saved again, and sending starts from then.
- Anything else, a timeout of five seconds included, is tried again after 10 seconds, doubling each time to at most 10
  minutes. Later events wait behind it.
- An event is given up once it is 24 hours old, or after five answers that refuse it: a 4xx other than 408, 410 and
  429. Refusals are counted apart from other failures, so timeouts and server errors never bring an event nearer to
  being given up; both counts start again for the next event. A receiver that is down keeps its events for a day, and one
  that refuses a single event lets the next through within a few minutes.
- When more than 50 events wait, the oldest are given up, so a receiver that comes back after a day gets at most 50, one
  after another.
- Given-up events are counted on the webhook page.

The receiver's answer is never read: a receiver, or a proxy before it, may echo the path and query it was asked for, token
and all. What a try came to is its status code and one of these, written by the server:

| Outcome | Shown |
| --- | --- |
| 2xx | `Delivered: HTTP 204.` |
| 3xx | `HTTP 302: redirected, and redirects are not followed.` |
| 410 | `HTTP 410: the receiver is gone; sending stopped.` |
| 408, 429 | `HTTP 429: the receiver asked to try again later.` |
| other 4xx | `HTTP 403: the receiver refused the event.` |
| 5xx | `HTTP 503: the receiver had an error.` |
| anything else | `HTTP 1xx: not a success.` |
| no answer | `No answer within 5 seconds.` |
| no connection | `The host name did not resolve.`, `Could not connect.`, `The TLS connection failed.`, `The proxy refused the connection.` or `The request failed.` |

Where sending stands is one row, `webhook`: the id it last finished, the tries and refusals of the next one and when it is
due, and how the last try went, in the words above. Settings are changed one save at a time. The loop writes that
row with conditional bulk updates, so its writes never wake the engine, and a change of settings meanwhile makes it record
nothing. Rows in `notifications` are never changed by it. A restart sends from where it stopped: an event whose try was
under way may be sent twice, with the same `id`, and none is lost.

Notifications are added by several writers, outside the engine's lock as well as inside it, so one with a lower id can
commit after one with a higher id. The loop never passes over it. Before it picks the next event, it takes, in a short
transaction of its own, `LOCK TABLE notifications IN SHARE MODE` with a `lock_timeout` of one second. Every insert holds
ROW EXCLUSIVE on the table from before it takes its id from the identity sequence until it commits, and SHARE waits for
all of them, so once the lock is granted no id below those it reads can still appear. It reads, commits and lets go
before it sends anything. When the lock is not granted within the second, it changes nothing and tries again ten
seconds later, or as soon as a notification is saved. When nothing waits, it reads once without the lock and stops
there.

Saving the first URL, or one after sending stopped, reads where "from then on" begins behind the same lock, in the
transaction that saves the settings. If a notification is still being added after a second, the save answers 503 and
changes nothing; save again.
