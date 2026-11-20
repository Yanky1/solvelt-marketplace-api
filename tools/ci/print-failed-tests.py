#!/usr/bin/env python3
"""Names the failed tests of a CI run: reads every results.trx under the tests folder and prints each failed test with its (shortened) message.

Output goes to the step's log, to GitHub annotations (the first ten failures, readable from the run's check output) and to $GITHUB_STEP_SUMMARY. It never fails the job by itself (the test step already did) and
prints nothing but test names and assertion messages. A .trx that is missing (a build failure, or a run stopped by the hang guard) is reported as such.
"""
import glob
import os
import sys
import xml.etree.ElementTree as ET

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
MAX_MESSAGE = 600
MAX_ANNOTATIONS = 10


def oneline(text: str, limit: int) -> str:
    text = " ".join((text or "").split())
    return text if len(text) <= limit else text[: limit - 3] + "..."


def main() -> int:
    files = sorted(glob.glob("**/results.trx", recursive=True))
    lines: list[str] = []
    failures: list[tuple[str, str]] = []
    if not files:
        lines.append("No results.trx was written: the build failed, or the run was stopped before the tests finished (see the hang guard output in the test step).")
    for path in files:
        try:
            root = ET.parse(path).getroot()
        except ET.ParseError as e:
            lines.append(f"{path}: not readable ({e})")
            continue
        counters = root.find("t:ResultSummary/t:Counters", NS)
        if counters is not None:
            lines.append(f"{path}: total {counters.get('total')}, passed {counters.get('passed')}, failed {counters.get('failed')}, not executed {counters.get('notExecuted')}")
        for r in root.iterfind(".//t:UnitTestResult", NS):
            if r.get("outcome") in ("Failed", "Error", "Timeout", "Aborted"):
                msg = r.findtext("t:Output/t:ErrorInfo/t:Message", default="", namespaces=NS)
                failures.append((r.get("testName", "?"), oneline(msg, MAX_MESSAGE)))
    for name, msg in failures:
        lines.append(f"FAILED {name}\n    {msg}")
    text = "\n".join(lines) if lines else "The test step failed but no failed test is recorded in the .trx."
    print(text)
    for name, msg in failures[:MAX_ANNOTATIONS]:
        print(f"::error title=Failed test::{name}: {msg}".replace("\r", " ").replace("\n", " ").replace("%", "%25"))
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as f:
            f.write("### Failed tests\n\n```\n" + text + "\n```\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
