from pathlib import Path

source_path = Path(__file__).with_name("issue1327_phase3_slice_a.py")
source = source_path.read_text(encoding="utf-8")
# The original staging generator used raw triple-quoted strings with a backslash
# after the opening delimiter. In a raw string that backslash is literal, so
# generated C# files began with '\\'. Strip that generator-only sentinel before
# executing the script. Product files themselves are otherwise unchanged.
source = source.replace("textwrap.dedent(r'''\\\\\n", "textwrap.dedent(r'''\n")
exec(compile(source, str(source_path), "exec"), {"__name__": "__main__", "__file__": str(source_path)})
