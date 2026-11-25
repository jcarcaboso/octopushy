import cases from "../../../contracts/examples/run-changes.json";
import { preparedChanges } from "./prepared";

// The server builds the same lines for its run summaries; these cases are what both must agree on.
describe("the shared run-changes cases", () => {
  it.each(cases.map((c) => [c.name, c] as const))("%s", (_, c) => {
    expect(preparedChanges(c.prepared, c.previous)).toEqual(c.changes);
  });
});
