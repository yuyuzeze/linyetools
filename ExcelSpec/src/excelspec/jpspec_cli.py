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


def _print_json(payload: object) -> None:
    typer.echo(to_json(payload))


def _fail(message: str, code: int = 1) -> None:
    typer.secho(message, fg=typer.colors.RED, err=True)
    raise typer.Exit(code)


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
    workbook: Path = typer.Argument(..., exists=True, readable=True, help="实际 XLSX"),
    template: Optional[Path] = typer.Option(
        None,
        "--template",
        "-t",
        help="模板文件或模板包目录；省略则自动匹配内置/目录模板",
    ),
    template_dir: Optional[Path] = typer.Option(
        None, "--template-dir", help="自动匹配用的模板目录"
    ),
    output: Path = typer.Option(Path("output"), "--output", "-o", help="输出目录"),
    formats: str = typer.Option(
        "json",
        "--format",
        "-f",
        help="额外导出格式，逗号分隔：json,md,html,jsonl（json 总会写 canonical.json）",
    ),
    asset_dir: Optional[Path] = typer.Option(None, "--asset-dir"),
    strict: bool = typer.Option(False, "--strict", help="warning 也视为失败"),
    minimum_score: Optional[float] = typer.Option(None, "--minimum-score"),
) -> None:
    """使用模板解析工作簿，写出 canonical.json（及可选 md/html/jsonl）。"""

    output.mkdir(parents=True, exist_ok=True)
    assets = asset_dir or (output / "assets")
    try:
        result = run_pipeline(
            workbook,
            template=template,
            template_directory=template_dir,
            asset_dir=assets,
            minimum_score=minimum_score,
        )
    except (PipelineValidationError, TemplateValidationError, Exception) as error:  # noqa: BLE001
        _fail(f"parse 失败: {error}")

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
        _fail("校验未通过，未写出 canonical.json")

    canonical = output / "canonical.json"
    export_document(result.document, canonical, "json")
    typer.echo(str(canonical.resolve()))

    requested = {
        part.strip().lower()
        for part in formats.split(",")
        if part.strip() and part.strip().lower() != "json"
    }
    for fmt in sorted(requested):
        destination = output / f"document.{ 'md' if fmt == 'markdown' else fmt }"
        try:
            export_document(result.document, destination, fmt)
        except Exception as error:  # noqa: BLE001
            _fail(f"导出 {fmt} 失败: {error}")
        typer.echo(str(destination.resolve()))

    (output / "diagnostics.json").write_text(
        to_json([item.to_dict() for item in diagnostics]) + "\n",
        encoding="utf-8",
    )
    if result.match and result.match.template:
        typer.echo(
            f"template: {result.match.template.template_id} v{result.match.template.version}"
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
