import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router";
import { describe, expect, it } from "vitest";
import type { Issue } from "../api/types";
import { DefinitionSheet, type AppliedDefinition } from "./DefinitionSheet";

const file = "labs/home/proxmox/pve01/guests/bb/guest.yaml";
const text = [
  "schemaVersion: 1",
  "type: vm",
  "# Sized for builds.",
  "hardware:",
  "  cores: 4",
  "  memory: 8192",
  "access:",
  "  sshKey: { store: proxmox/pve01/bb/ssh-key, version: 2 }",
  "",
].join("\n");

function renderSheet(props: { text: string | null; issues?: Issue[]; applied?: AppliedDefinition[] }) {
  return render(
    <MemoryRouter>
      <DefinitionSheet file={file} kind="guest" {...props} />
    </MemoryRouter>,
  );
}

describe("DefinitionSheet", () => {
  it("reads the definition as sections of labelled values, with a secret linked to its folder", () => {
    renderSheet({ text });
    expect(screen.getByRole("heading", { name: "Hardware" })).toBeInTheDocument();
    expect(screen.getByText("Sized for builds.")).toBeInTheDocument();
    expect(screen.getByText("Cores").nextElementSibling).toHaveTextContent("4");
    expect(screen.getByText("8192 MiB")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "proxmox/pve01/bb/ssh-key" })).toHaveAttribute("href", "/variables?folder=proxmox/pve01/bb");
    expect(screen.getByText("v2")).toBeInTheDocument();
    // Read, not shown: no line of YAML on the page.
    expect(screen.queryByText("memory")).toBeNull();
  });

  it("shows the value as written and the author's comment, both", () => {
    renderSheet({ text: "hardware:\n  memory: 8192   # for the build cache\n  disks:\n    - { size: 32, datastore: local-lvm }   # data volume\n" });
    expect(screen.getByText("8192 MiB")).toBeInTheDocument();
    expect(screen.getByText("for the build cache")).toBeInTheDocument();
    expect(screen.getByText("on local-lvm")).toBeInTheDocument();
    expect(screen.getByText("data volume")).toBeInTheDocument();
  });

  it("says what was last applied for a value that differs, only in the sections that run applies", () => {
    const then = "type: container\nhardware:\n  memory: 4096\naccess:\n  sshKey: { store: proxmox/pve01/bb/ssh-key, version: 1 }\n";
    renderSheet({ text, applied: [{ text: then, sections: ["hardware"] }] });
    expect(screen.getByText("Memory").parentElement).toHaveTextContent("Last applied: 4 GiB");
    // Cores was not in the file then.
    expect(screen.getByText("Cores").parentElement).toHaveTextContent("Not in the last apply");
    expect(screen.getByText("2 values not applied yet")).toBeInTheDocument();
    // The type and the key changed too, but this kind of run applies neither.
    expect(screen.getByText("Type").parentElement).not.toHaveTextContent("Last applied");
    expect(screen.getByText("SSH key").parentElement).not.toHaveTextContent("Last applied");
  });

  it("marks nothing when the last apply had the same values", () => {
    renderSheet({ text, applied: [{ text: text.replace("# Sized for builds.", "# Another comment."), sections: ["general", "hardware", "access"] }] });
    expect(screen.queryByText(/not applied yet/)).toBeNull();
    expect(screen.queryByText(/Last applied/)).toBeNull();
  });

  it("opens the YAML at a section's lines, marked", async () => {
    renderSheet({ text });
    await userEvent.click(screen.getByRole("button", { name: "lines 4–6" }));
    const dialog = screen.getByRole("dialog", { name: "Definition" });
    const marked = dialog.querySelectorAll(".definition__line--marked");
    expect([...marked].map((line) => line.querySelector(".definition__number")?.textContent)).toEqual(["4", "5", "6"]);
    await userEvent.click(within(dialog).getByRole("button", { name: "Close" }));
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("shows the YAML with its marks when the definition has an error", () => {
    const issues: Issue[] = [{ severity: "error", message: "cores must be a number", file, line: 5, address: null }];
    renderSheet({ text, issues });
    expect(screen.queryByRole("heading", { name: "Hardware" })).toBeNull();
    expect(screen.getByText(/Error on line 5: cores must be a number/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "View YAML" })).toBeNull();
  });

  it("shows the YAML when the text is not a mapping, and says when there is no text", () => {
    renderSheet({ text: "- a\n- b\n" });
    expect(screen.getByRole("region", { name: `${file}, read-only` })).toBeInTheDocument();
    renderSheet({ text: null });
    expect(screen.getByText("The definition text is not available for the current revision.")).toBeInTheDocument();
  });
});
