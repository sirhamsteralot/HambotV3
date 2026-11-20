import mistralai.workflows as workflows


async def main() -> None:
    workflow_classes = workflows.discover_all_workflows_in_package("worker.workflows")
    if not workflow_classes:
        raise RuntimeError("No workflows discovered in worker.workflows")
    await workflows.run_worker(workflow_classes)


if __name__ == "__main__":
    import asyncio

    asyncio.run(main())
