import { describe, expect, it } from "vitest";
import { check, detect, formatSummary, pairs, splitLine, summarize } from "./format";

const env = ["# outline", "NODE_ENV=production", "PORT=3000", "export SMTP_HOST=smtp.example.org", "SMTP_PASSWORD=h4Jc9sNb2Yq"].join("\n");
const yml = ["oidc:", "  issuer: https://auth.example.org", "  scopes: [openid, email]", "storage:", "  - bucket"].join("\n");

describe("detect", () => {
  it("reads NAME=value lines as .env", () => {
    expect(detect(env)).toBe("env");
  });

  it("reads name: value and list items as YAML", () => {
    expect(detect(yml)).toBe("yaml");
  });

  it("reads anything else, a single token or an empty value as plain text", () => {
    expect(detect("kV3n8QpLx2Tz")).toBe("text");
    expect(detect("")).toBe("text");
    expect(detect("# only a comment\n\n")).toBe("text");
  });

  it("prefers .env when both kinds of line are as common", () => {
    expect(detect("A=1\nb: 2")).toBe("env");
  });
});

describe("check", () => {
  it("finds lines without =, bad names and names set twice in an env file", () => {
    expect(check("A=1\nnothing here\n1BAD=2\n\n# A=3\nA=4", "env")).toEqual([
      { line: 2, message: "there is no = between the name and the value" },
      { line: 3, message: "a name may only hold letters, digits and _" },
      { line: 6, message: "A is already set on line 1" },
    ]);
  });

  it("accepts a clean env file", () => {
    expect(check(env, "env")).toEqual([]);
  });

  it("reports the YAML parser's error with its line", () => {
    expect(check(yml, "yaml")).toEqual([]);
    const problems = check("a: 1\nb:\n  c: 2\n d: 3", "yaml");
    expect(problems).toHaveLength(1);
    expect(problems[0].line).toBe(4);
    expect(problems[0].message).toMatch(/indentation/);
  });

  it("has nothing to say about plain text", () => {
    expect(check("anything = at all\n: really", "text")).toEqual([]);
  });
});

describe("summarize", () => {
  it("is empty when nothing changed", () => {
    expect(summarize(env, env)).toBe("");
  });

  it("names up to three variables of an env file", () => {
    const after = env.replace("h4Jc9sNb2Yq", "new").replace("PORT=3000", "PORT=3000\nSMTP_PORT=587");
    // In the order of the new value.
    expect(summarize(env, after)).toBe("SMTP_PORT added, SMTP_PASSWORD changed.");
    expect(summarize(env, env.replace("PORT=3000\n", ""))).toBe("PORT removed.");
  });

  it("counts lines once more than three variables changed", () => {
    const after = "A=1\nB=2\nC=3\nD=4";
    expect(summarize("", after, "env")).toBe("1 line changed, 3 lines added.");
  });

  it("counts lines changed, added and removed for other formats", () => {
    expect(summarize("a: 1\nb: 2", "a: 1\nb: 3\nc: 4")).toBe("1 line changed, 1 line added.");
    expect(summarize("x\ny\nz", "x")).toBe("2 lines removed.");
    expect(summarize("old", "new")).toBe("1 line changed.");
  });

  it("says so when only the order changed", () => {
    expect(summarize("a\nb", "b\na", "text")).toBe("The lines are in another order.");
  });
});

describe("helpers", () => {
  it("lists the pairs of an env file without export", () => {
    expect(pairs("export A=1\n# c\nB==x\nnone")).toEqual([
      { name: "A", value: "1" },
      { name: "B", value: "=x" },
    ]);
  });

  it("describes a value with no problems", () => {
    expect(formatSummary(env, "env")).toBe("Reads as .env with 4 variables, no problems.");
    expect(formatSummary(yml, "yaml")).toBe("Reads as YAML, no problems.");
    expect(formatSummary("x", "text")).toBe("Kept exactly as entered.");
    expect(formatSummary(" ", "text")).toBe("Nothing entered yet.");
  });

  it("splits a line into name, separator and the rest", () => {
    expect(splitLine("PORT=3000", "env")).toEqual({ name: "PORT", sep: "=", rest: "3000", comment: false });
    expect(splitLine("  issuer: https://a.example", "yaml")).toEqual({ name: "  issuer", sep: ": ", rest: "https://a.example", comment: false });
    expect(splitLine("# note", "env").comment).toBe(true);
    expect(splitLine("PORT=3000", "text")).toEqual({ name: "", sep: "", rest: "PORT=3000", comment: false });
  });
});
