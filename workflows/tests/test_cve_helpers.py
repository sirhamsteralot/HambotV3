import sys
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "apps" / "worker" / "src"))

from worker.workflows.hambot_cve_fix_pr import (
    CveFinding,
    ScanReport,
    _version_key,
    branch_name_for,
    compute_bump,
    parse_scan_log,
    plan_actions,
    pr_title_for,
    report_markdown,
)

SAMPLE_LOG = """=== Dependency security scan summary ===
--- hambotv2 ---
CRITICAL: 1
HIGH: 1
CRITICAL CVE-2026-1111 Discord.Net 3.20.2 -> fixed 3.21.0
HIGH CVE-2026-2222 Other.Pkg 2.0.0 -> fixed 2.1.0
--- bottests ---
MEDIUM: 3
--- image ---
CRITICAL CVE-2026-3333 openssl 3.0.0 -> fixed unknown
CRITICAL CVE-2026-1111 Discord.Net 3.20.2 -> fixed 3.21.0
CRITICAL CVE-2026-4444 Newtonsoft.Json 13.0.1 -> fixed 13.0.4
"""

HAMBAOT_CSPROJ = """<Project Sdk="Microsoft.NET.Sdk.Worker">
  <ItemGroup>
    <PackageReference Include="Discord.Net" Version="3.20.2" />
    <PackageReference Include="Serilog.Sinks.Console" Version="6.1.1" />
  </ItemGroup>
</Project>
"""

BOTTESTS_CSPROJ = """<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore" Version="10.0.13" />
    <PackageReference Include="Moq" Version="4.21.0" />
  </ItemGroup>
</Project>
"""


def test_parse_scan_log_keeps_only_critical_and_dedupes():
    findings = parse_scan_log(SAMPLE_LOG)
    ids = [(f.cve_id, f.package, f.source) for f in findings]
    assert ("CVE-2026-1111", "Discord.Net", "hambotv2") in ids
    assert ("CVE-2026-3333", "openssl", "image") in ids
    assert ("CVE-2026-4444", "Newtonsoft.Json", "image") in ids
    assert all(f.severity == "CRITICAL" for f in findings)
    assert len([f for f in findings if f.cve_id == "CVE-2026-1111"]) == 1
    assert not any(f.cve_id == "CVE-2026-2222" for f in findings)


def test_parse_scan_log_high_only_when_asked():
    findings = parse_scan_log(SAMPLE_LOG, severities=("CRITICAL", "HIGH"))
    assert any(f.cve_id == "CVE-2026-2222" and f.severity == "HIGH" for f in findings)


def test_compute_bump_rewrites_direct_reference():
    new_text, old = compute_bump(HAMBAOT_CSPROJ, "Discord.Net", "3.21.0")
    assert old == "3.20.2"
    assert 'Include="Discord.Net" Version="3.21.0"' in new_text
    assert 'Include="Serilog.Sinks.Console" Version="6.1.1"' in new_text


def test_compute_bump_no_match_returns_unchanged():
    new_text, old = compute_bump(HAMBAOT_CSPROJ, "Moq", "9.9.9")
    assert old is None
    assert new_text == HAMBAOT_CSPROJ


def test_version_key_orders_numerically():
    assert _version_key("10.0.14") > _version_key("10.0.9")
    assert _version_key("3.21.0") > _version_key("3.20.2")


def _findings():
    return [
        CveFinding(cve_id="CVE-2026-1111", severity="CRITICAL", package="Discord.Net",
                   installed_version="3.20.2", fixed_version="3.21.0", source="hambotv2"),
        CveFinding(cve_id="CVE-2026-3333", severity="CRITICAL", package="openssl",
                   installed_version="3.0.0", fixed_version="unknown", source="image"),
        CveFinding(cve_id="CVE-2026-4444", severity="CRITICAL", package="Newtonsoft.Json",
                   installed_version="13.0.1", fixed_version="13.0.4", source="image"),
        CveFinding(cve_id="CVE-2026-5555", severity="CRITICAL", package="Moq",
                   installed_version="4.21.0", fixed_version="4.21.0", source="bottests"),
    ]


def test_plan_actions_bumps_direct_references_and_notes_the_rest():
    files = {"HambotV2/HambotV2.csproj": HAMBAOT_CSPROJ, "BotTests/BotTests.csproj": BOTTESTS_CSPROJ}
    actions = plan_actions(_findings(), files)
    by_pkg = {a.package: a for a in actions}
    assert by_pkg["Discord.Net"].applied
    assert by_pkg["Discord.Net"].file == "HambotV2/HambotV2.csproj"
    assert by_pkg["Discord.Net"].from_version == "3.20.2"
    assert by_pkg["Discord.Net"].to_version == "3.21.0"
    assert by_pkg["openssl"].note == "no fixed version reported; manual remediation needed"
    assert not by_pkg["openssl"].applied
    assert "not a direct PackageReference" in by_pkg["Newtonsoft.Json"].note
    assert by_pkg["Moq"].note == "already on the fixed version"
    assert not by_pkg["Moq"].applied


def test_plan_actions_picks_highest_fixed_version():
    findings = [
        CveFinding(cve_id="CVE-2026-1", severity="CRITICAL", package="Discord.Net",
                   installed_version="3.20.2", fixed_version="3.21.0", source="hambotv2"),
        CveFinding(cve_id="CVE-2026-2", severity="CRITICAL", package="Discord.Net",
                   installed_version="3.20.2", fixed_version="3.22.1", source="image"),
    ]
    actions = plan_actions(findings, {"HambotV2/HambotV2.csproj": HAMBAOT_CSPROJ})
    assert len(actions) == 1
    assert actions[0].to_version == "3.22.1"
    assert "CVE-2026-1" in actions[0].cve_id and "CVE-2026-2" in actions[0].cve_id


def test_branch_and_pr_title():
    report = ScanReport(job_full_name="security/hambotv2-scan", build_number=42, build_result="UNSTABLE",
                        findings=_findings())
    when = datetime(2026, 3, 2, 6, 0, tzinfo=timezone.utc)
    assert branch_name_for("fix/critical-cves-", report, when) == "fix/critical-cves-20260302-b42"
    title = pr_title_for(report)
    assert "4 CRITICAL CVEs" in title and "#42" in title


def test_report_markdown_lists_findings_and_actions():
    report = ScanReport(job_full_name="security/hambotv2-scan", build_number=42, build_result="UNSTABLE",
                        findings=_findings())
    actions = plan_actions(_findings(), {"HambotV2/HambotV2.csproj": HAMBAOT_CSPROJ})
    md = report_markdown(report, actions, "https://github.com/sirhamsteralot/HambotV3")
    assert "CVE-2026-1111" in md and "Discord.Net" in md
    assert "3.20.2 -> 3.21.0" in md
    assert "security/hambotv2-scan" in md and "#42" in md
