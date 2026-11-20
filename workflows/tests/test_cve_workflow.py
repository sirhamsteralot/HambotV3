import sys
import types
import uuid
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "apps" / "worker" / "src"))

import mistralai.workflows as workflows
import mistralai.workflows.testing as wftest
from mistralai.workflows.plugins.mistralai.connectors import interceptor as conn_interceptor
from mistralai.workflows.plugins.mistralai.connectors.interceptor import ConnectorAuthInterceptor
from worker.workflows.hambot_cve_fix_pr import (
    CveFinding,
    CveFixResult,
    CveInput,
    FixPlan,
    HambotCveFixPrWorkflow,
    ScanReport,
)

TASK_QUEUE = "test-task-queue"  # must match config.worker.deployment_name set by the setup_test_config fixture


@pytest.fixture
def fake_connector_preflight(monkeypatch):
    async def fake_resolve(name, run_as=None):
        return types.SimpleNamespace(id="conn-jenkins-test")

    async def fake_list_creds(connector_id, run_as=None):
        return types.SimpleNamespace(credentials=[types.SimpleNamespace(name="default")])

    async def fake_list_tools(connector_id, credentials_name=None, run_as=None):
        return True

    async def fake_auth_methods(connector_id, run_as=None):
        return []

    monkeypatch.setattr(conn_interceptor, "connector_resolve", fake_resolve)
    monkeypatch.setattr(conn_interceptor, "connector_list_user_credentials", fake_list_creds)
    monkeypatch.setattr(conn_interceptor, "connector_list_tools", fake_list_tools)
    monkeypatch.setattr(conn_interceptor, "connector_get_auth_methods", fake_auth_methods)


@pytest.fixture
def fake_activities():
    calls = []

    async def find_security_scan_job(input: CveInput) -> str:
        calls.append("find_job")
        return "security/hambotv2-scan"

    async def fetch_critical_cves(input: CveInput, job_full_name: str) -> ScanReport:
        calls.append("fetch")
        assert job_full_name == "security/hambotv2-scan"
        return ScanReport(
            job_full_name=job_full_name,
            build_number=42,
            build_result="UNSTABLE",
            findings=[
                CveFinding(cve_id="CVE-2026-1111", severity="CRITICAL", package="Discord.Net",
                           installed_version="3.20.2", fixed_version="3.21.0", source="hambotv2")
            ],
        )

    async def prepare_fix_branch(input: CveInput, report: ScanReport) -> FixPlan:
        calls.append("prepare")
        assert report.build_number == 42
        return FixPlan(repo_dir="/tmp/hambot-fake", report_file="docs/security/cve-fix-report-20260302.md",
                       changed_files=["HambotV2/HambotV2.csproj", "docs/security/cve-fix-report-20260302.md"])

    async def push_and_open_pr(input: CveInput, report: ScanReport, plan: FixPlan) -> CveFixResult:
        calls.append("push")
        return CveFixResult(report=report, actions=plan.actions, branch_name="fix/critical-cves-20260302-b42",
                            pr_url="https://github.com/sirhamsteralot/HambotV3/pull/7", pr_number=7)

    async def cleanup_repo(plan: FixPlan) -> None:
        calls.append("cleanup")

    def wrap(name, fn):
        return workflows.activity(name=name, _skip_registering=True)(fn)

    return {
        "find_security_scan_job": wrap("find_security_scan_job", find_security_scan_job),
        "fetch_critical_cves": wrap("fetch_critical_cves", fetch_critical_cves),
        "prepare_fix_branch": wrap("prepare_fix_branch", prepare_fix_branch),
        "push_and_open_pr": wrap("push_and_open_pr", push_and_open_pr),
        "cleanup_repo": wrap("cleanup_repo", cleanup_repo),
    }, calls, wrap


async def _run(temporal_env, input: CveInput, activities):
    interceptors = [ConnectorAuthInterceptor([HambotCveFixPrWorkflow])]
    async with wftest.create_test_worker(
        temporal_env,
        workflows=[HambotCveFixPrWorkflow],
        activities=list(activities.values()),
        task_queue=TASK_QUEUE,
        interceptors=interceptors,
    ):
        handle = await temporal_env.client.start_workflow(
            "hambot-cve-fix-pr",
            input,
            id=f"test-cve-{uuid.uuid4()}",
            task_queue=TASK_QUEUE,
        )
        return await handle.result()


async def test_workflow_full_pipeline(temporal_env, fake_connector_preflight, fake_activities):
    activities, calls, _wrap = fake_activities
    result = CveFixResult.model_validate(await _run(temporal_env, CveInput(), activities))
    assert calls == ["find_job", "fetch", "prepare", "push", "cleanup"]
    assert result.pr_number == 7
    assert result.report.job_full_name == "security/hambotv2-scan"
    assert result.report.findings[0].cve_id == "CVE-2026-1111"


async def test_workflow_no_critical_findings_short_circuits(temporal_env, fake_connector_preflight, fake_activities):
    activities, calls, wrap = fake_activities

    async def empty_scan(input: CveInput, job_full_name: str) -> ScanReport:
        calls.append("fetch")
        return ScanReport(job_full_name=job_full_name, build_number=41, build_result="SUCCESS")

    activities["fetch_critical_cves"] = wrap("fetch_critical_cves", empty_scan)
    result = CveFixResult.model_validate(await _run(temporal_env, CveInput(), activities))
    assert calls == ["find_job", "fetch"]
    assert result.report.findings == []
    assert result.pr_url == ""
