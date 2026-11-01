"""Records what a run did, for the agent to report. It never records a task's result.

A task's name is templated, so it can hold a secret. This file stays in memory, in the private
directory `octopushy-ansible` owns, and the agent masks it before anything keeps it. So nothing
here cuts a value to size: part of a secret would no longer match. Only the agent cuts.
"""
import json
import os

from ansible.plugins.callback import CallbackBase

DOCUMENTATION = """
    name: octopushy_recap
    type: notification
    short_description: Writes a summary of the run for Octopushy
    description:
      - Writes counts, the failed task's name and a declared outcome to OCTOPUSHY_RESULT_FILE.
"""

# ssh's reason comes last, after a banner, and no module has a part in this text.
MAX_UNREACHABLE = 1024
MAX_TEXT = 8192


def bounded(value):
    """All of a text, or none: it has to fit on the agent's line."""
    text = str(value)
    return text if len(text) <= MAX_TEXT else "(too long to report)"


class CallbackModule(CallbackBase):
    CALLBACK_VERSION = 2.0
    CALLBACK_TYPE = "notification"
    CALLBACK_NAME = "octopushy_recap"
    CALLBACK_NEEDS_ENABLED = True

    def __init__(self):
        super().__init__()
        self.path = os.environ.get("OCTOPUSHY_RESULT_FILE")
        self.result = {
            # A task reached the guest, so something may have happened there.
            "reached": False,
            "finished": False,
            "tasks": 0,
        }

    def write(self):
        if not self.path:
            return
        temporary = self.path + ".partial"
        try:
            with open(temporary, "w", encoding="utf-8") as stream:
                json.dump(self.result, stream)
            os.replace(temporary, self.path)
        except OSError:
            # A recap that stops following the run says less happened than did. No recap
            # at all is reported as uncertain, which is the truth.
            for path in (temporary, self.path):
                try:
                    os.unlink(path)
                except OSError:
                    pass
            self.path = None

    def v2_playbook_on_start(self, playbook):
        self.write()

    def v2_playbook_on_task_start(self, task, is_conditional):
        self.result["tasks"] += 1
        self.write()

    def reached(self):
        if not self.result["reached"]:
            self.result["reached"] = True
            self.write()

    def v2_runner_on_ok(self, result, **kwargs):
        self.reached()

    def v2_runner_on_skipped(self, result, **kwargs):
        pass

    def v2_runner_on_failed(self, result, ignore_errors=False):
        self.reached()
        if not ignore_errors:
            self.result["failedTask"] = bounded(result.task_name)
            self.write()

    def v2_runner_on_unreachable(self, result):
        # This says how the connection failed; it comes from ssh, not from a module. ssh ends
        # with the reason, after a banner that can be long, so the end is what is kept.
        message = result._result.get("msg") if hasattr(result, "_result") else None
        self.result["unreachable"] = str(message or "")[-MAX_UNREACHABLE:]
        self.result["unreachableTask"] = bounded(result.task_name)
        self.write()

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
            self.result["outcome"] = str(custom["octopushy_outcome"])
        if "octopushy_detail" in custom:
            self.result["detail"] = bounded(custom["octopushy_detail"])
        self.result["finished"] = True
        self.write()
