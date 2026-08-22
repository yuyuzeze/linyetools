"""jpspec: product CLI for Japanese Excel specification conversion."""

from __future__ import annotations

import json
import sys
from pathlib import Path
from typing import Optional

import typer

from . import __version__
from .inspection import write_inspection
from .pipeline import (
    PipelineValidationError,
    discover_inputs,
    export_document,
    run_pipeline,
)
from .serialization import to_json
from .template_pack import (
    compare_workbooks,
    init_template_pack,
)
from .templates import TemplateValidationError
from .validate import validate_document, validate_ir_data
from .models.document_ir import DocumentIR


app = typer.Typer(
    add_completion=False,
    no_args_is_help=True,
    help="日语 Excel 式样书命令行核心（inspect / parse / validate / template）",
)
template_app = typer.Typer(no_args_is_help=True, help="模板包工具")
app.add_typer(template_app, name="template")

_FORMAT_SUFFIX = {
    "json": ".json",
    "md": ".md",
    "markdown": ".md",
    "html": ".html",
    "jsonl": ".jsonl",
    "kb-jsonl": ".jsonl",
    "semantic-json": ".semantic.json",
    "chunks": ".chunks.jsonl",
}


def _print_json(payload: object) -> None:
    typer.echo(to_json(payload))


def _fail(message: str, code: int = 1) -> None:
    typer.secho(message, fg=typer.colors.RED, err=True)
    raise typer.Exit(code)


def _asset_dir_for(output: Path, source: Path, asset_dir: Path | None) -> Path:
    if asset_dir is not None:
        return asset_dir
    return output / f"asset.{source.stem}"


def _parse_one(
    workbook: Path,
    *,
    template: Path | None,
    template_dir: Path | None,
    output: Path,
    formats: str,
    asset_dir: Path | None,
    strict: bool,
    minimum_score: float | None,
    strict_schema: bool,
    ingest_engine: str,
    mode: str | None,
    profile: Path | None,
    auto_legacy_template: bool,
    cache: bool,
    cache_dir: Path | None,
) -> None:
    output.mkdir(parents=True, exist_ok=True)
    assets = _asset_dir_for(output, workbook, asset_dir)
    assets.mkdir(parents=True, exist_ok=True)
    try:
        result = run_pipeline(
            workbook,
            template=template,
            template_directory=template_dir,
            asset_dir=assets,
            minimum_score=minimum_score,
            strict_schema=strict_schema,
            ingest_engine=ingest_engine,
            mode=mode,
            profile=profile,
            auto_legacy_template=auto_legacy_template,
            cache=cache,
            cache_dir=cache_dir or output,
        )
    except (PipelineValidationError, TemplateValidationError, Exception) as error:  # noqa: BLE001
        _fail(f"parse 失败 ({workbook.name}): {error}")

    diagnostics = result.all_diagnostics()
    errors = [item for item in diagnostics if item.severity.value == "error"]
    warnings = [item for item in diagnostics if item.severity.value == "warning"]
    if errors or (strict and warnings):
        for item in diagnostics:
            typer.secho(
                f"[{item.severity.value}] {item.code}: {item.message}",
                fg=typer.colors.RED if item.severity.value == "error" else typer.colors.YELLOW,
                err=True,
            )
        _fail(f"校验未通过，未写出输出: {workbook.name}")

    stem = workbook.stem
    canonical = output / f"{stem}.json"
    export_document(result.document, canonical, "json")
    typer.echo(str(canonical.resolve()))

    requested = {
        part.strip().lower()
        for part in formats.split(",")
        if part.strip() and part.strip().lower() not in {"json", "canonical"}
    }
    for fmt in sorted(requested):
        suffix = _FORMAT_SUFFIX.get(fmt)
        if suffix is None:
            _fail(f"不支持的导出格式: {fmt}")
        destination = output / f"{stem}{suffix}"
        try:
            export_document(result.document, destination, fmt)
        except Exception as error:  # noqa: BLE001
            _fail(f"导出 {fmt} 失败 ({workbook.name}): {error}")
        typer.echo(str(destination.resolve()))

    (output / f"{stem}.diagnostics.json").write_text(
        to_json(
            {
                "processing": result.processing,
                "diagnostics": [item.to_dict() for item in diagnostics],
            }
        )
        + "\n",
        encoding="utf-8",
    )
    typer.echo(
        "mode: "
        f"{result.processing.get('processing_mode')}"
        f"/{result.processing.get('detection_mode')}"
        f" profile={result.processing.get('profile_id')}"
        f" ingest={result.processing.get('ingest_engine')}"
    )
    if result.match and result.match.template:
        typer.echo(
            f"template: {result.match.template.template_id} v{result.match.template.version}"
        )


@app.callback()
def main_callback(
    version: bool = typer.Option(
        False, "--version", help="显示版本并退出", is_eager=True
    ),
) -> None:
    if version:
        typer.echo(f"jpspec {__version__}")
        raise typer.Exit(0)


@app.command("inspect")
def inspect_cmd(
    workbook: Path = typer.Argument(..., exists=True, readable=True, help="样例或实际 XLSX"),
    output: Path = typer.Option(
        Path("inspection"),
        "--output",
        "-o",
        help="检查结果输出目录",
    ),
    asset_dir: Optional[Path] = typer.Option(
        None, "--asset-dir", help="嵌入图输出目录；默认 output/assets"
    ),
) -> None:
    """输出 Excel 结构：workbook.json / sheets/*.json / preview/*.html"""

    try:
        root = write_inspection(workbook, output, asset_dir=asset_dir)
    except Exception as error:  # noqa: BLE001 - CLI boundary
        _fail(f"inspect 失败: {error}")
    typer.echo(str(root.resolve()))
    typer.echo(f"workbook: {root / 'workbook.json'}")
    typer.echo(f"sheets:   {root / 'sheets'}")
    typer.echo(f"preview:  {root / 'preview'}")


@app.command("parse")
def parse_cmd(
    workbook: Path = typer.Argument(
        ...,
        exists=True,
        readable=True,
        help="实际 XLSX 文件，或包含多个 XLSX 的目录（批量）",
    ),
    template: Optional[Path] = typer.Option(
        None,
        "--template",
        "-t",
        help="模板文件或模板包目录；省略则自动匹配内置/目录模板",
    ),
    legacy_template: Optional[Path] = typer.Option(
        None, "--legacy-template", help="旧坐标模板（等价 --template）"
    ),
    mode: Optional[str] = typer.Option(
        None, "--mode", help="零配置检测模式：fast|auto|visual（与 --template 互斥）"
    ),
    profile: Optional[Path] = typer.Option(
        None, "--profile", help="语义 Profile 文件（仅在零配置模式下生效）"
    ),
    auto_legacy_template: bool = typer.Option(
        False,
        "--auto-legacy-template",
        help="显式启用旧版 bundled 模板自动匹配（默认零配置 fast）",
    ),
    cache: bool = typer.Option(
        False, "--cache/--no-cache", help="启用内容哈希缓存（默认 output/.excelspec-cache）"
    ),
    cache_dir: Optional[Path] = typer.Option(
        None, "--cache-dir", help="缓存目录（默认 output/）"
    ),
    template_dir: Optional[Path] = typer.Option(
        None, "--template-dir", help="自动匹配用的模板目录"
    ),
    output: Path = typer.Option(Path("output"), "--output", "-o", help="输出目录"),
    formats: str = typer.Option(
        "json,md",
        "--format",
        "-f",
        help="导出格式，逗号分隔：json,md,html,jsonl（json 总会写 {stem}.json）",
    ),
    asset_dir: Optional[Path] = typer.Option(
        None,
        "--asset-dir",
        help="嵌入图目录；默认每个源文件写到 output/asset.{stem}/",
    ),
    strict: bool = typer.Option(False, "--strict", help="warning 也视为失败"),
    strict_schema: bool = typer.Option(
        False,
        "--strict-schema",
        help="对 XLSX 也执行完整 DocumentIR JSON Schema 校验（默认仅结构检查）",
    ),
    ingest_engine: str = typer.Option(
        "auto",
        "--ingest-engine",
        help="XLSX 摄取引擎：auto(默认)|sparse|legacy",
    ),
    minimum_score: Optional[float] = typer.Option(None, "--minimum-score"),
) -> None:
    """使用模板解析工作簿，写出 {stem}.json / {stem}.md 等（支持批量目录）。"""

    try:
        sources = discover_inputs([workbook], include_json=False)
    except Exception as error:  # noqa: BLE001
        _fail(f"parse 失败: {error}")
    if not sources:
        _fail("没有找到可解析的 XLSX 文件")

    effective_template = template or legacy_template
    for source in sources:
        _parse_one(
            source,
            template=effective_template,
            template_dir=template_dir,
            output=output,
            formats=formats,
            asset_dir=asset_dir,
            strict=strict,
            minimum_score=minimum_score,
            strict_schema=strict_schema,
            ingest_engine=ingest_engine,
            mode=mode,
            profile=profile,
            auto_legacy_template=auto_legacy_template,
            cache=cache,
            cache_dir=cache_dir,
        )


@app.command("validate")
def validate_cmd(
    canonical: Path = typer.Argument(
        ..., exists=True, readable=True, help="canonical.json 或 DocumentIR JSON"
    ),
    template: Optional[Path] = typer.Option(
        None, "--template", "-t", help="可选模板包/文件，用于业务规则校验"
    ),
    strict: bool = typer.Option(False, "--strict"),
    json_output: bool = typer.Option(False, "--json", help="输出机器可读结果"),
) -> None:
    """验证 canonical.json 是否符合 DocumentIR / 模板业务规则。"""

    from .templates import load_template

    try:
        data = json.loads(canonical.read_text(encoding="utf-8-sig"))
        schema_diagnostics = validate_ir_data(data)
        document = DocumentIR.from_dict(data)
        selected = load_template(template) if template is not None else None
        result = validate_document(document, selected)
        diagnostics = [*schema_diagnostics, *result.diagnostics]
    except Exception as error:  # noqa: BLE001
        _fail(f"validate 失败: {error}")

    payload = {
        "source": str(canonical),
        "valid": not any(item.severity.value == "error" for item in diagnostics)
        and not (
            strict and any(item.severity.value == "warning" for item in diagnostics)
        ),
        "diagnostics": [item.to_dict() for item in diagnostics],
    }
    if json_output:
        _print_json(payload)
    else:
        typer.echo("valid" if payload["valid"] else "invalid")
        for item in diagnostics:
            typer.echo(f"[{item.severity.value}] {item.code}: {item.message}")
    if not payload["valid"]:
        raise typer.Exit(1)


@app.command("audit")
def audit_cmd(
    workbook: Path = typer.Argument(..., exists=True, readable=True, help="XLSX 文件"),
    output: Path = typer.Option(
        Path("audit.html"), "--output", "-o", help="审计 HTML 输出路径"
    ),
    mode: str = typer.Option("fast", "--mode", help="fast|auto|visual"),
    profile: Optional[Path] = typer.Option(None, "--profile", help="语义 Profile"),
) -> None:
    """生成人工可检查的 HTML 审计报告（仅评估用途，不改变正式 exporter）。"""

    from .eval.audit import build_audit_html

    try:
        path = build_audit_html(workbook, output, mode=mode, profile=profile)
    except Exception as error:  # noqa: BLE001
        _fail(f"audit 失败: {error}")
    typer.echo(str(path.resolve()))


@template_app.command("init")
def template_init_cmd(
    workbook: Path = typer.Argument(..., exists=True, readable=True, help="样例 XLSX"),
    output: Path = typer.Option(
        ...,
        "--output",
        "-o",
        help="模板包输出目录，例如 templates/screen-design-v1",
    ),
    type_name: Optional[str] = typer.Option(
        None,
        "--type",
        help="文档类型：screen-design / report-design / generic",
    ),
    template_id: Optional[str] = typer.Option(None, "--id", help="模板 ID"),
    force: bool = typer.Option(False, "--force", help="允许写入非空目录"),
) -> None:
    """从样例 Excel 生成模板包：template.xlsx + template.yaml + schema.json + examples。"""

    try:
        root = init_template_pack(
            workbook,
            output,
            document_type=type_name,
            template_id=template_id,
            force=force,
        )
    except Exception as error:  # noqa: BLE001
        _fail(f"template init 失败: {error}")
    typer.echo(str(root.resolve()))
    typer.echo("created: template.xlsx, template.yaml, schema.json, examples/, prompts/")


@template_app.command("compare")
def template_compare_cmd(
    template_xlsx: Path = typer.Argument(..., exists=True, readable=True),
    actual_xlsx: Path = typer.Argument(..., exists=True, readable=True),
    json_output: bool = typer.Option(True, "--json/--no-json", help="默认输出 JSON"),
) -> None:
    """比较模板样例与实际文件的 sheet / 表头 / 合并密度差异。"""

    try:
        payload = compare_workbooks(template_xlsx, actual_xlsx)
    except Exception as error:  # noqa: BLE001
        _fail(f"template compare 失败: {error}")
    if json_output:
        _print_json(payload)
    else:
        typer.echo("similar" if payload["similar"] else "different")
        for name in payload["sheets_only_in_template"]:
            typer.echo(f"- only in template: {name}")
        for name in payload["sheets_only_in_actual"]:
            typer.echo(f"- only in actual: {name}")
    raise typer.Exit(0 if payload["similar"] else 1)


def main(argv: list[str] | None = None) -> int:
    try:
        app(args=argv, standalone_mode=False)
    except typer.Exit as exit_error:
        return int(exit_error.exit_code or 0)
    return 0


if __name__ == "__main__":
    sys.exit(main())
