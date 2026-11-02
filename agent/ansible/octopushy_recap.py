"""Reports what a run did, for the agent. It never records a task's result.

The recap is one line on stderr when the playbook ends, kept in memory until then, so a run
that dies leaves none and the agent calls it uncertain. There is nothing to go stale or to
stay on a disk. A task's name is templated, so it can hold a secret: the agent masks all text
here, and nothing here cuts a value to size, because part of a secret would no longer match.
"""
import json
import os

from ansible.plugins.callback import CallbackBase

DOCUMENTATION = """
    name: octopushy_recap
    type: notification
    short_description: Reports a summary of the run to Octopushy
    description:
      - Writes counts, the failed task's name and a declared outcome to stderr as one line.
"""

PREFIX = "octopushy-recap: "
# Bytes as written, escaping included. Five of these and the counts have to fit on one line of
# the agent's 32 KiB, or the only record of how the run ended would be dropped.
MAX_TEXT = 5120


def encoded(text):
    try:
        return text.encode("utf-8", "surrogateescape")
    except UnicodeEncodeError:
        return text.encode("utf-8", "replace")


def bounded(value):
    """All of a text, or none: it has to fit on the agent's line."""
    text = str(value)
    fits = len(encoded(json.dumps(text, ensure_ascii=False))) <= MAX_TEXT
    return text if fits else "(too long to report)"


class CallbackModule(CallbackBase):
    CALLBACK_VERSION = 2.0
    CALLBACK_TYPE = "notification"
    CALLBACK_NAME = "octopushy_recap"
    CALLBACK_NEEDS_ENABLED = True

    def __init__(self):
        super().__init__()
        self.enabled = bool(os.environ.get("OCTOPUSHY_RECAP"))
        # `reached`: a task reached the guest, so something may have happened there.
        self.result = {"reached": False, "tasks": 0}

    def v2_playbook_on_task_start(self, task, is_conditional):
        self.result["tasks"] += 1

    def v2_runner_on_ok(self, result, **kwargs):
        self.result["reached"] = True

    def v2_runner_on_failed(self, result, ignore_errors=False):
        self.result["reached"] = True
        if not ignore_errors:
            self.result["failedTask"] = bounded(result.task_name)

    def v2_runner_on_unreachable(self, result):
        # The first says where the run was lost. A later one, in a play that ignored it, shows
        # that the run went on past a task nobody knows the end of, so none of them is proof
        # that nothing happened: a login refused now can follow a task that removed the login.
        if "unreachable" in self.result:
            self.result["reached"] = True
            return
        # This says how the connection failed; it comes from ssh, not from a module.
        message = result._result.get("msg") if hasattr(result, "_result") else None
        self.result["unreachable"] = bounded(message or "")
        self.result["unreachableTask"] = bounded(result.task_name)

    def v2_playbook_on_stats(self, stats):
        totals = {"ok": 0, "changed": 0, "unreachable": 0, "failures": 0, "skipped": 0, "rescued": 0, "ignored": 0}
        for host in sorted(stats.processed):
            summary = stats.summarize(host)
            for name in totals:
                totals[name] += summary.get(name, 0)
        self.result["stats"] = totals
        custom = {}
        for scope in stats.custom.values():
            custom.update(scope)
        if "octopushy_outcome" in custom:
            self.result["outcome"] = bounded(custom["octopushy_outcome"])
        if "octopushy_detail" in custom:
            self.result["detail"] = bounded(custom["octopushy_detail"])
        if self.enabled:
            line = PREFIX + json.dumps(self.result, ensure_ascii=False) + "\n"
            os.write(2, encoded(line))
