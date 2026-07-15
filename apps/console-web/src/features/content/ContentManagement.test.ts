import { describe, expect, it } from "vitest";
import { businessDateForDefinition, parseDefinition } from "./ContentManagement";

describe("content editor helpers", () => {
  it("accepts an object definition and rejects arrays or malformed JSON", () => {
    expect(parseDefinition('{"schemaVersion":1}')).toEqual({
      definition: { schemaVersion: 1 },
      error: null
    });
    expect(parseDefinition("[]").error).toContain("顶层 JSON");
    expect(parseDefinition("{").error).toContain("JSON 无法解析");
  });

  it("derives the publication date in the definition time zone", () => {
    const instant = new Date("2026-07-15T16:30:00.000Z");
    expect(businessDateForDefinition({ timeZoneId: "Asia/Shanghai" }, instant)).toBe("2026-07-16");
    expect(businessDateForDefinition({ timeZoneId: "UTC" }, instant)).toBe("2026-07-15");
  });
});
