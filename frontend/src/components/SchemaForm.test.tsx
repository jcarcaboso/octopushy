import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { describe, expect, it } from "vitest";
import type { JsonObject, Schema, UiHints } from "../lib/jsonSchema";
import worker from "../test/fixtures/octopushy-worker.inputs.schema.json";
import { SchemaForm, type FieldIssue } from "./SchemaForm";

const values: JsonObject = {
  hardware: { cores: 2, memory: 4096, disks: [{ size: 32 }] },
  network: { bridge: "vmbr0", mode: "static", address: "10.1.0.40/24", gateway: "10.1.0.1" },
  inputs: { worker_server_url: "https://octopushy.lan:8443", worker_agent_version: "0.5.0", worker_agent_labels: ["home"] },
};

/** The form with its value kept here, and the latest value it emitted. */
function Harness({ schema, initial, ui, issues, seen }: { schema: Schema; initial: JsonObject; ui?: UiHints; issues?: FieldIssue[];
  seen: { value: JsonObject | null } }) {
  const [value, setValue] = useState(initial);
  return (
    <SchemaForm schema={schema} ui={ui} value={value} issues={issues}
      onChange={(next) => {
        seen.value = next;
        setValue(next);
      }} />
  );
}

function group(name: RegExp | string) {
  return screen.getByRole("group", { name });
}

describe("SchemaForm", () => {
  it("renders the octopushy-worker schema: $ref, arrays of objects and enums", () => {
    render(<Harness schema={worker as Schema} initial={values} seen={{ value: null }} />);
    const hardware = group(/^hardware/);
    expect(within(hardware).getByLabelText(/^memory/)).toHaveValue(4096);
    expect(within(hardware).getByText("MiB")).toBeInTheDocument();
    expect(within(hardware).getByText("1 to 128")).toBeInTheDocument();
    // An array of objects: each disk is its own group, with its own fields.
    expect(within(group(/^disks\[1\]/)).getByLabelText(/^size/)).toHaveValue(32);
    // An enum is a choice.
    expect(within(group(/^network/)).getByLabelText(/^mode/)).toHaveDisplayValue("static");
    // A $ref into $defs is followed, and the description next to it is shown.
    expect(within(group(/^network/)).getByLabelText(/^reservedAddress/)).toHaveValue("");
    expect(screen.getByText(/Expected IPv4 address of an externally configured DHCP reservation/)).toBeInTheDocument();
    // Required fields are marked.
    expect(within(group(/^inputs/)).getByText("worker_server_url").closest("label")).toHaveTextContent("*");
  });

  it("round-trips values: an untouched form gives back exactly what it was given", async () => {
    const seen = { value: null as JsonObject | null };
    render(<Harness schema={worker as Schema} initial={values} seen={seen} />);
    const memory = within(group(/^hardware/)).getByLabelText(/^memory/);
    await userEvent.clear(memory);
    await userEvent.type(memory, "4096");
    expect(seen.value).toEqual(values);
  });

  it("edits nested values, adds and removes items, and leaves cleared optional fields out", async () => {
    const seen = { value: null as JsonObject | null };
    render(<Harness schema={worker as Schema} initial={values} seen={seen} />);
    const user = userEvent.setup();

    const memory = within(group(/^hardware/)).getByLabelText(/^memory/);
    await user.clear(memory);
    await user.type(memory, "8192");
    await user.click(screen.getByRole("button", { name: "Add to disks" }));
    await user.type(within(group(/^disks\[2\]/)).getByLabelText(/^size/), "100");
    await user.selectOptions(within(group(/^network/)).getByLabelText(/^mode/), "dhcp");
    await user.click(screen.getByRole("button", { name: "Add to worker_agent_labels" }));
    const labels = within(group(/^worker_agent_labels/)).getAllByRole("textbox");
    await user.type(labels[1], "gpu");
    await user.click(screen.getByRole("button", { name: "Remove worker_agent_labels[1]" }));

    expect(seen.value).toEqual({
      hardware: { cores: 2, memory: 8192, disks: [{ size: 32 }, { size: 100 }] },
      network: { bridge: "vmbr0", mode: "dhcp", address: "10.1.0.40/24", gateway: "10.1.0.1" },
      inputs: { worker_server_url: "https://octopushy.lan:8443", worker_agent_version: "0.5.0", worker_agent_labels: ["gpu"] },
    });

    await user.clear(within(group(/^network/)).getByLabelText(/^gateway/));
    expect((seen.value!.network as JsonObject).gateway).toBeUndefined();
  });

  it("shows the server's issues next to their fields and patterns as hints", async () => {
    render(<Harness schema={worker as Schema} initial={values} seen={{ value: null }}
      issues={[{ pointer: "/hardware/memory", message: "256 is the minimum" }, { pointer: "/inputs", message: "Required properties are missing" }]} />);
    const memory = within(group(/^hardware/)).getByLabelText(/^memory/);
    expect(memory).toHaveAttribute("aria-invalid", "true");
    expect(within(memory.closest(".field")! as HTMLElement).getByRole("alert")).toHaveTextContent("256 is the minimum");
    expect(within(group(/^inputs/)).getAllByRole("alert")[0]).toHaveTextContent("Required properties are missing");

    const version = within(group(/^inputs/)).getByLabelText(/^worker_agent_version/);
    await userEvent.clear(version);
    await userEvent.type(version, "latest");
    await userEvent.tab();
    expect(screen.getByText("This does not match the pattern the workflow expects.")).toBeInTheDocument();
  });

  it("edits what it cannot render as YAML, and keeps keys the schema does not name", async () => {
    const schema: Schema = {
      type: "object",
      properties: {
        inputs: {
          type: "object",
          properties: {
            machine_features: { type: "array", uniqueItems: true, items: { enum: ["ai-tools", "cli-tools", "t3"] } },
            machine_t3_settings: { type: "object" },
            choice: { oneOf: [{ type: "string" }, { type: "integer" }] },
          },
        },
      },
    };
    const seen = { value: null as JsonObject | null };
    const initial: JsonObject = { inputs: { machine_features: ["t3"], machine_t3_settings: { port: 3773 }, choice: 3, legacy: "kept" } };
    render(<Harness schema={schema} initial={initial} seen={seen} />);

    // An array of choices is a set of checkboxes.
    await userEvent.click(screen.getByRole("checkbox", { name: "ai-tools" }));
    expect((seen.value!.inputs as JsonObject).machine_features).toEqual(["t3", "ai-tools"]);

    const settings = screen.getByLabelText(/^machine_t3_settings/);
    expect(settings).toHaveValue("port: 3773");
    expect(screen.getByText("YAML: Its keys are free-form.")).toBeInTheDocument();
    expect(screen.getByLabelText(/^choice/)).toHaveValue("3");
    expect(screen.getByLabelText(/^Other keys/)).toHaveValue("legacy: kept");

    await userEvent.clear(settings);
    await userEvent.type(settings, "port: 4000{enter}theme: dark");
    expect((seen.value!.inputs as JsonObject).machine_t3_settings).toEqual({ port: 4000, theme: "dark" });
    expect((seen.value!.inputs as JsonObject).legacy).toBe("kept");
  });

  it("follows the workflow's ui hints: labels, help, order, groups and hidden fields", () => {
    const ui: UiHints = {
      order: ["inputs", "hardware"],
      fields: {
        "hardware.memory": { label: "Memory", help: "In MiB; at least 256." },
        inputs: { order: ["worker_agent_version"] },
        "inputs.worker_server_url": { group: "Server" },
        "inputs.worker_agent_digest": { group: "Server" },
        "inputs.worker_min_free_disk_mb": { widget: "hidden" },
      },
    };
    render(<Harness schema={worker as Schema} initial={values} ui={ui} seen={{ value: null }} />);
    const sections = screen.getAllByRole("group").filter((g) => g.parentElement?.classList.contains("schema-form__fields")
      && g.parentElement.parentElement?.classList.contains("schema-form"));
    expect(sections.map((s) => s.getAttribute("data-pointer"))).toEqual(["/inputs", "/hardware", "/network"]);
    expect(screen.getByLabelText(/^Memory/)).toHaveValue(4096);
    expect(screen.getByText("In MiB; at least 256.")).toBeInTheDocument();
    expect(within(group("Server")).getByLabelText(/^worker_server_url/)).toBeInTheDocument();
    expect(screen.queryByLabelText(/^worker_min_free_disk_mb/)).toBeNull();
    const inputs = within(group(/^inputs/)).getAllByRole("textbox").map((t) => t.closest("[data-pointer]")!.getAttribute("data-pointer"));
    expect(inputs[0]).toBe("/inputs/worker_agent_version");
  });
});
