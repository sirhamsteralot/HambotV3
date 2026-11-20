"""Weekly CRITICAL-CVE fix PR for the HambotV3 repository.

Scheduled every Monday (see the schedule on the deployment). Per run it:

1. Uses the Jenkins MCP connector to find the security scan pipeline job that
   scans sirhamsteralot/HambotV3 (the job whose SCM script path is
   ``Jenkinsfile.security``) and reads the Report-stage lines of its last
   completed build.
2. Extracts the CRITICAL findings (CVE id, package, installed -> fixed version).
3. Opens a draft PR on the hambot repo that bumps every direct PackageReference
   with a known fixed version to that version, plus a markdown report listing
   all CRITICAL findings and the suggested fix for each.

The Jenkins scan itself runs weekly in Jenkins (cron in Jenkinsfile.security);
this workflow only reads its results through the Jenkins MCP.
"""

from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import tempfile
from datetime import datetime, timezone
from typing import Any, Dict, List, Optional, Tuple

from pydantic import BaseModel, Field

import mistralai.workflows as workflows
from mistralai.workflows import Depends
from mistralai.workflows.plugins.mistralai.connectors import (
    ToolCallClient,
    connector,
    uses_connectors,
)

REPO_URL_DEFAULT = "https://github.com/sirhamsteralot/HambotV3"
BASE_BRANCH_DEFAULT = "master"
SECURITY_SCRIPT_PATH = "Jenkinsfile.security"
GITHUB_TOKEN_ENV = "HAMBOT_GITHUB_TOKEN"
GITHUB_TOKEN_ENV_FALLBACK = "hee_github_token"
SCAN_SOURCES = ("hambotv2", "bottests", "image")
REPORT_FILE_DIR = "docs/security"
LOG_TAIL_LINES = 4000
MAX_FINDINGS = 500

jenkins_connector = connector("jenkins_a59a", run_as="deployment")


class CveInput(BaseModel):
    repo_url: str = REPO_URL_DEFAULT
    base_branch: str = BASE_BRANCH_DEFAULT
    branch_prefix: str = "fix/critical-cves-"
    jenkins_job: str = ""  # explicit full job path; empty = auto-discover via SCM URL
    report_file_dir: str = REPORT_FILE_DIR
    dry_run: bool = False


class CveFinding(BaseModel):
    cve_id: str
    severity: str
    package: str
    installed_version: str
    fixed_version: str
    source: str = ""


class ScanReport(BaseModel):
    job_full_name: str = ""
    build_number: Optional[int] = None
    build_result: str = ""
    build_timestamp: Optional[int] = None
    findings: List[CveFinding] = Field(default_factory=list)


class FixAction(BaseModel):
    cve_id: str
    package: str
    file: str = ""
    from_version: str = ""
    to_version: str = ""
    applied: bool = False
    note: str = ""


class FixPlan(BaseModel):
    repo_dir: str = ""
    actions: List[FixAction] = Field(default_factory=list)
    report_file: str = ""
    changed_files: List[str] = Field(default_factory=list)


class CveFixResult(BaseModel):
    report: ScanReport = Field(default_factory=ScanReport)
    actions: List[FixAction] = Field(default_factory=list)
    branch_name: str = ""
    pr_url: str = ""
    pr_number: int = 0
    dry_run: bool = False


FINDING_RE = re.compile(
    r"^(CRITICAL|HIGH)\s+(\S+)\s+(\S+)\s+(\S+)\s+->\s+fixed\s+(\S+)\s*$"
)
SECTION_RE = re.compile(r"^---\s*(\w+)\s*---\s*$")
CVE_ID_RE = re.compile(r"^(CVE-\d{4}-\d{4,7}|GHSA-[0-9a-z-]+|[A-Z]+-\d+-\d+)$", re.IGNORECASE)


def _version_key(version: str) -> Tuple[Tuple[int, ...], str]:
    nums = tuple(int(p) for p in re.findall(r"\d+", version))
    return (nums, version)


def parse_scan_log(text: str, severities: Tuple[str, ...] = ("CRITICAL",)) -> List[CveFinding]:
    findings: List[CveFinding] = []
    seen = set()
    source = ""
    for line in text.splitlines():
        line = line.strip()
        section = SECTION_RE.match(line)
        if section:
            source = section.group(1)
            continue
        m = FINDING_RE.match(line)
        if not m:
            continue
        severity, cve_id, package, installed, fixed = m.groups()
        if severity not in severities or not CVE_ID_RE.match(cve_id):
            continue
        key = (cve_id, package, installed)
        if key in seen:
            continue
        seen.add(key)
        findings.append(
            CveFinding(
                cve_id=cve_id,
                severity=severity,
                package=package,
                installed_version=installed,
                fixed_version=fixed,
                source=source,
            )
        )
    return findings[:MAX_FINDINGS]


def compute_bump(csproj_text: str, package: str, new_version: str) -> Tuple[str, Optional[str]]:
    tag_re = re.compile(r'<PackageReference\b[^>]*\bInclude="' + re.escape(package) + r'"[^>]*>')
    m = tag_re.search(csproj_text)
    if not m:
        return csproj_text, None
    tag = m.group(0)
    vm = re.search(r'\bVersion="([^"]+)"', tag)
    if not vm:
        return csproj_text, None
    new_tag = tag[: vm.start(1)] + new_version + tag[vm.end(1):]
    return csproj_text[: m.start()] + new_tag + csproj_text[m.end():], vm.group(1)


def plan_actions(findings: List[CveFinding], files: Dict[str, str]) -> List[FixAction]:
    targets: Dict[Tuple[str, str], str] = {}
    nofix: Dict[str, List[str]] = {}
    for f in sorted(findings, key=lambda x: x.cve_id):
        fixed = "" if f.fixed_version.lower() in ("unknown", "none", "") else f.fixed_version
        if not fixed:
            nofix.setdefault(f.package, []).append(f.cve_id)
            continue
        for path, text in files.items():
            if '<PackageReference' in text and f'"{f.package}"' in text:
                key = (path, f.package)
                if key not in targets or _version_key(fixed) > _version_key(targets[key]):
                    targets[key] = fixed
    actions: List[FixAction] = []
    bumped_packages = {package for (_, package) in targets}
    for (path, package), new_version in sorted(targets.items()):
        _, old = compute_bump(files[path], package, new_version)
        cves = sorted(f.cve_id for f in findings if f.package == package and f.fixed_version.lower() not in ("unknown", "none", ""))
        if not old:
            actions.append(
                FixAction(
                    cve_id=", ".join(cves),
                    package=package,
                    file=path,
                    to_version=new_version,
                    applied=False,
                    note="package not found as direct reference",
                )
            )
        elif old == new_version:
            actions.append(
                FixAction(
                    cve_id=", ".join(cves),
                    package=package,
                    file=path,
                    from_version=old,
                    to_version=new_version,
                    applied=False,
                    note="already on the fixed version",
                )
            )
        else:
            actions.append(
                FixAction(
                    cve_id=", ".join(cves),
                    package=package,
                    file=path,
                    from_version=old,
                    to_version=new_version,
                    applied=True,
                )
            )
    for package, cves in sorted(nofix.items()):
        if package in bumped_packages:
            continue
        actions.append(
            FixAction(
                cve_id=", ".join(sorted(cves)),
                package=package,
                applied=False,
                note="no fixed version reported; manual remediation needed",
            )
        )
    transitive: Dict[str, List[str]] = {}
    for f in findings:
        if f.fixed_version.lower() in ("unknown", "none", ""):
            continue
        if f.package not in bumped_packages and not any(
            f'"{f.package}"' in text for text in files.values()
        ):
            transitive.setdefault(f.package, []).append(f.cve_id)
    for package, cves in sorted(transitive.items()):
        actions.append(
            FixAction(
                cve_id=", ".join(sorted(cves)),
                package=package,
                applied=False,
                note="not a direct PackageReference; fix via parent package or base image",
            )
        )
    actions.sort(key=lambda a: (not a.applied, a.file, a.package))
    return actions


def report_markdown(report: ScanReport, actions: List[FixAction], repo_url: str) -> str:
    lines = [
        "# CRITICAL CVE fix report (weekly security scan)",
        "",
        f"- Jenkins job: `{report.job_full_name}`",
        f"- Scan build: #{report.build_number} (result: {report.build_result or 'unknown'})",
        f"- Repository: {repo_url}",
        "",
        "## CRITICAL findings",
        "",
        "| CVE | Package | Installed | Fixed | Source | Action |",
        "|---|---|---|---|---|---|",
    ]
    for f in report.findings:
        action = next((a for a in actions if f.cve_id in a.cve_id and a.package == f.package), None)
        if action and action.applied:
            act = f"bump `{f.package}` {action.from_version} -> {action.to_version} in `{action.file}`"
        elif action:
            act = action.note or "see PR body"
        else:
            act = "no direct PackageReference; manual fix"
        fixed = f.fixed_version if f.fixed_version.lower() != "unknown" else "_none reported_"
        lines.append(f"| {f.cve_id} | {f.package} | {f.installed_version} | {fixed} | {f.source or '?'} | {act} |")
    lines += [
        "",
        "## Suggested fixes",
        "",
        "Direct `PackageReference` bumps to the scanner-reported fixed version are applied in this PR.",
        "Findings without a fixed version, or in packages that are only transitive / base-image layers,",
        "need a manual decision (upgrade the parent package or the base image).",
        "",
        "---",
        "_Generated by the `hambot-cve-fix-pr` Mistral workflow from the Jenkins security scan._",
    ]
    return "\n".join(lines)


def branch_name_for(prefix: str, report: ScanReport, when: datetime) -> str:
    build = f"b{report.build_number}" if report.build_number else "latest"
    return f"{prefix}{when.strftime('%Y%m%d')}-{build}"


def pr_title_for(report: ScanReport) -> str:
    n = len(report.findings)
    return f"fix: address {n} CRITICAL CVE{'s' if n != 1 else ''} from weekly security scan (build #{report.build_number})"


def pr_body_for(report: ScanReport, actions: List[FixAction], repo_url: str) -> str:
    return report_markdown(report, actions, repo_url)


def _payload(resp: Any) -> Any:
    if isinstance(resp, str):
        try:
            return json.loads(resp)
        except json.JSONDecodeError:
            return resp
    if isinstance(resp, dict):
        if resp.get("isError"):
            raise RuntimeError(f"Jenkins tool error: {resp}")
        structured = resp.get("structuredContent")
        if structured is not None:
            return structured
        content = resp.get("content")
        if isinstance(content, list):
            texts = [c.get("text", "") for c in content if isinstance(c, dict) and c.get("type") == "text"]
            joined = "\n".join(texts)
            try:
                return json.loads(joined)
            except json.JSONDecodeError:
                return joined
        return resp
    return resp


async def _call(jenkins: ToolCallClient, tool: str, arguments: Dict[str, Any]) -> Any:
    return _payload(await jenkins.call_tool(tool_name=tool, arguments=arguments))


def _items(payload: Any) -> List[Dict[str, Any]]:
    if isinstance(payload, list):
        return [p for p in payload if isinstance(p, dict)]
    if isinstance(payload, dict):
        for key in ("jobs", "items", "results"):
            if isinstance(payload.get(key), list):
                return [p for p in payload[key] if isinstance(p, dict)]
    return []


def _log_text(payload: Any) -> str:
    if isinstance(payload, str):
        return payload
    if isinstance(payload, dict):
        lines = payload.get("lines")
        if isinstance(lines, list):
            out = []
            for entry in lines:
                if isinstance(entry, dict):
                    out.append(str(entry.get("line", entry.get("text", ""))))
                else:
                    out.append(str(entry))
            return "\n".join(out)
    return json.dumps(payload) if payload is not None else ""


def _run(cmd: List[str], cwd: Optional[str] = None) -> str:
    env = dict(os.environ)
    env.setdefault("GIT_LFS_SKIP_SMUDGE", "1")
    proc = subprocess.run(cmd, cwd=cwd, env=env, capture_output=True, text=True, timeout=600)
    if proc.returncode != 0:
        raise RuntimeError(f"command failed ({proc.returncode}): {' '.join(cmd)}\n{proc.stderr[-2000:]}")
    return proc.stdout


@workflows.activity()
async def find_security_scan_job(input: CveInput, jenkins: ToolCallClient = Depends(jenkins_connector)) -> str:
    if input.jenkins_job:
        job = await _call(jenkins, "getJob", {"jobFullName": input.jenkins_job})
        full_name = job.get("fullName", input.jenkins_job) if isinstance(job, dict) else input.jenkins_job
        return str(full_name)
    urls = {input.repo_url, input.repo_url + ".git", input.repo_url.removesuffix(".git")}
    candidates: List[str] = []
    for url in sorted(urls):
        skip = 0
        while True:
            page = await _call(jenkins, "findJobsWithScmUrl", {"scmUrl": url, "skip": skip, "limit": 10})
            jobs = _items(page)
            if not jobs:
                break
            for j in jobs:
                name = j.get("fullName") or j.get("name")
                if name and name not in candidates:
                    candidates.append(str(name))
            if len(jobs) < 10:
                break
            skip += len(jobs)
    if not candidates:
        raise RuntimeError(f"No Jenkins jobs found with SCM URL pointing at {input.repo_url}")
    matches: List[str] = []
    for name in candidates:
        scm = await _call(jenkins, "getJobScm", {"jobFullName": name})
        if SECURITY_SCRIPT_PATH in json.dumps(scm):
            matches.append(name)
    if not matches:
        matches = [c for c in candidates if "security" in c.lower()] or candidates
    matches.sort(key=lambda n: ("security" not in n.lower(), n))
    return matches[0]


@workflows.activity()
async def fetch_critical_cves(input: CveInput, job_full_name: str, jenkins: ToolCallClient = Depends(jenkins_connector)) -> ScanReport:
    job = await _call(jenkins, "getJob", {"jobFullName": job_full_name})
    build_ref = {}
    if isinstance(job, dict):
        build_ref = job.get("lastCompletedBuild") or job.get("lastBuild") or {}
    build_number = build_ref.get("number") if isinstance(build_ref, dict) else None
    build_args: Dict[str, Any] = {"jobFullName": job_full_name}
    if build_number:
        build_args["buildNumber"] = build_number
    build = await _call(jenkins, "getBuild", build_args)
    if isinstance(build, dict) and build.get("number"):
        build_number = build.get("number")
    log_args: Dict[str, Any] = {"jobFullName": job_full_name, "skip": -LOG_TAIL_LINES, "limit": LOG_TAIL_LINES}
    if build_number:
        log_args["buildNumber"] = build_number
    log = await _call(jenkins, "getBuildLog", log_args)
    findings = parse_scan_log(_log_text(log))
    if not findings:
        search_args: Dict[str, Any] = {
            "jobFullName": job_full_name,
            "pattern": r"^(CRITICAL|HIGH)\s+\S+\s+\S+\s+\S+\s+->\s+fixed\s+\S+",
            "useRegex": True,
            "maxMatches": 1000,
        }
        if build_number:
            search_args["buildNumber"] = build_number
        search = await _call(jenkins, "searchBuildLog", search_args)
        findings = parse_scan_log(json.dumps(search))
    report = ScanReport(job_full_name=job_full_name, findings=findings)
    if isinstance(build, dict):
        report.build_number = build.get("number", build_number)
        report.build_result = str(build.get("result") or "")
        report.build_timestamp = build.get("timestamp")
    else:
        report.build_number = build_number
    return report


@workflows.activity()
async def prepare_fix_branch(input: CveInput, report: ScanReport) -> FixPlan:
    repo_dir = tempfile.mkdtemp(prefix="hambot-cve-")
    _run(["git", "clone", "--quiet", "--branch", input.base_branch, input.repo_url, repo_dir])
    files = {
        "HambotV2/HambotV2.csproj": "",
        "BotTests/BotTests.csproj": "",
    }
    for path in files:
        with open(os.path.join(repo_dir, path), "r", errors="replace") as fh:
            files[path] = fh.read()
    actions = plan_actions(report.findings, files)
    changed: List[str] = []
    for action in actions:
        if action.applied and action.file:
            target = os.path.join(repo_dir, action.file)
            with open(target, "r", errors="replace") as fh:
                text = fh.read()
            new_text, _ = compute_bump(text, action.package, action.to_version)
            with open(target, "w") as fh:
                fh.write(new_text)
            if action.file not in changed:
                changed.append(action.file)
    when = datetime.now(timezone.utc)
    report_rel = f"{input.report_file_dir}/cve-fix-report-{when.strftime('%Y%m%d')}.md"
    report_abs = os.path.join(repo_dir, report_rel)
    os.makedirs(os.path.dirname(report_abs), exist_ok=True)
    with open(report_abs, "w") as fh:
        fh.write(report_markdown(report, actions, input.repo_url) + "\n")
    changed.append(report_rel)
    return FixPlan(repo_dir=repo_dir, actions=actions, report_file=report_rel, changed_files=changed)


@workflows.activity()
async def push_and_open_pr(input: CveInput, report: ScanReport, plan: FixPlan) -> CveFixResult:
    result = CveFixResult(report=report, actions=plan.actions, dry_run=input.dry_run)
    if input.dry_run:
        return result
    token = (
        os.environ.get(GITHUB_TOKEN_ENV, "")
        or os.environ.get(GITHUB_TOKEN_ENV.lower(), "")
        or os.environ.get(GITHUB_TOKEN_ENV_FALLBACK, "")
    )
    if not token:
        raise RuntimeError(
            f"Missing {GITHUB_TOKEN_ENV}: bind the workspace secret holding a "
            "GitHub PAT (repo contents + pull requests write) to the deployment."
        )
    repo_dir = plan.repo_dir
    _run(["git", "config", "user.name", "hambot-cve-fix-bot"], repo_dir)
    _run(["git", "config", "user.email", "noreply@users.noreply.github.com"], repo_dir)
    branch = branch_name_for(input.branch_prefix, report, datetime.now(timezone.utc))
    _run(["git", "checkout", "-q", "-b", branch], repo_dir)
    _run(["git", "add", "--"] + plan.changed_files, repo_dir)
    _run(["git", "commit", "-q", "-m", pr_title_for(report)], repo_dir)
    owner_repo = re.sub(r"^https://github\.com/", "", input.repo_url).removesuffix(".git")
    push_url = f"https://x-access-token:{token}@github.com/{owner_repo}.git"
    _run(["git", "push", "--quiet", push_url, f"HEAD:refs/heads/{branch}"], repo_dir)
    result.branch_name = branch

    import httpx

    api = f"https://api.github.com/repos/{owner_repo}/pulls"
    headers = {
        "Authorization": f"Bearer {token}",
        "Accept": "application/vnd.github+json",
        "X-GitHub-Api-Version": "2022-11-28",
    }
    with httpx.Client(timeout=60) as http:
        resp = http.post(
            api,
            headers=headers,
            json={
                "title": pr_title_for(report),
                "body": pr_body_for(report, plan.actions, input.repo_url),
                "head": branch,
                "base": input.base_branch,
                "draft": True,
            },
        )
        if resp.status_code == 422 and "A pull request already exists" in resp.text:
            q = http.get(api, headers=headers, params={"head": f"{owner_repo.split('/')[0]}:{branch}", "state": "open"})
            if q.status_code == 200 and q.json():
                pr = q.json()[0]
                result.pr_url, result.pr_number = pr["html_url"], pr["number"]
        else:
            resp.raise_for_status()
            pr = resp.json()
            result.pr_url, result.pr_number = pr["html_url"], pr["number"]
    return result


@workflows.activity()
async def cleanup_repo(plan: FixPlan) -> None:
    shutil.rmtree(plan.repo_dir, ignore_errors=True)


@workflows.workflow.define(
    name="hambot-cve-fix-pr",
    workflow_display_name="Hambot weekly CRITICAL CVE fix PR",
    workflow_description=(
        "Weekly (Mondays): reads the latest Jenkins security scan of the HambotV3 "
        "repo through the Jenkins MCP, collects the CRITICAL CVEs, and opens a draft "
        "PR bumping the affected direct package references to the fixed versions."
    ),
)
@uses_connectors(jenkins_connector)
class HambotCveFixPrWorkflow:
    @workflows.workflow.entrypoint
    async def run(self, input: CveInput) -> CveFixResult:
        job = await find_security_scan_job(input)
        report = await fetch_critical_cves(input, job)
        if not report.findings:
            return CveFixResult(report=report, dry_run=input.dry_run)
        plan = await prepare_fix_branch(input, report)
        result = await push_and_open_pr(input, report, plan)
        await cleanup_repo(plan)
        return result
