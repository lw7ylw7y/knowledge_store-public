#!/usr/bin/env python3
"""Turns the .trx files that `dotnet test` writes into a short markdown summary for the GitHub Actions run page.

    python scripts/ci_summary.py <title> <folder with .trx files>

It prints a table of totals and, for every failed test, its name and the first lines of its message. It never fails the
build itself: the test step does that.
"""
import glob
import os
import sys
import xml.etree.ElementTree as ET

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def main() -> int:
    title = sys.argv[1] if len(sys.argv) > 1 else "Tests"
    folder = sys.argv[2] if len(sys.argv) > 2 else "TestResults"
    files = sorted(glob.glob(os.path.join(folder, "*.trx")))
    print(f"### {title}\n")
    if not files:
        print("No test results were written.")
        return 0

    total = passed = failed = skipped = 0
    failures = []
    for path in files:
        root = ET.parse(path).getroot()
        counters = root.find(".//t:ResultSummary/t:Counters", NS)
        if counters is not None:
            total += int(counters.get("total", 0))
            passed += int(counters.get("passed", 0))
            failed += int(counters.get("failed", 0))
        for result in root.findall(".//t:UnitTestResult", NS):
            if result.get("outcome") == "Failed":
                message = result.find(".//t:ErrorInfo/t:Message", NS)
                text = (message.text or "").strip().splitlines()[:3] if message is not None else []
                failures.append((result.get("testName", "?"), " ".join(text)))

    skipped = total - passed - failed          # skipped tests are not counted as passed or failed
    print("| Total | Passed | Failed | Skipped |")
    print("| ---: | ---: | ---: | ---: |")
    print(f"| {total} | {passed} | {failed} | {skipped} |\n")
    if failures:
        print("**Failed tests**\n")
        for name, text in failures:
            print(f"- `{name}`: {text[:300]}")
        print()
    return 0


if __name__ == "__main__":
    sys.exit(main())
