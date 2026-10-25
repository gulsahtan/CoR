"""Run real analyzers and emit normalized findings on stdout; errors never become []."""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET


def category(check):
    value = check.lower()
    if any(x in value for x in ('null', 'none', 'nullptr')):
        return 'null-safety'
    if any(x in value for x in ('cast', 'type', 'conversion')):
        return 'type-mismatch'
    if any(x in value for x in ('dead', 'unreachable')):
        return 'control-flow'
    if any(x in value for x in ('bound', 'index', 'buffer')):
        return 'boundary'
    return 'other'


def finding(tool, check, file, method, severity):
    return dict(tool=tool, checkId=check, file=file.replace('\\', '/'), method=method,
                category=category(check), severity=severity)


def spotbugs(xml):
    root = ET.fromstring(xml)
    if root.tag != 'BugCollection':
        raise ValueError('Expected SpotBugs BugCollection XML')
    errors = root.find('Errors')
    if errors is not None and (int(errors.get('errors', '0')) or int(errors.get('missingClasses', '0')) or errors.find('MissingClass') is not None):
        raise ValueError('SpotBugs reported analysis errors')
    results = []
    for bug in root.findall('BugInstance'):
        method = bug.find('Method')
        source = bug.find('SourceLine')
        if source is None and method is not None:
            source = method.find('SourceLine')
        method_id = '<global>' if method is None else '.'.join([method.get('classname', ''), method.get('name', '')]) + method.get('signature', '')
        results.append(finding('SpotBugs', bug.attrib['type'], source.get('sourcepath', '') if source is not None else '',
                               method_id, 4 - int(bug.get('priority', '2'))))
    return results


def enclosing_method(path, line):
    # Stable C-function signature, excluding line numbers. Limitations documented in docs/experiments.md.
    lines = Path(path).read_text(encoding='utf-8', errors='replace').splitlines()[:line]
    matches = re.findall(r'^\s*([\w *]+\b(?!if\b|while\b|for\b)\w+\s*\([^;{}]*\))\s*\{', '\n'.join(lines), re.M)
    return re.sub(r'\s+', ' ', matches[-1]).strip() if matches else '<global>'


def clang_findings(text):
    results = []
    for file, line, level, check in re.findall(r'^(.+?):(\d+):\d+: (warning|error): .*?\[([^\]]+)\]\s*$', text, re.M):
        path = Path(file).resolve()
        relative = os.path.relpath(path, Path.cwd())
        results.append(finding('clang-tidy', check, relative, enclosing_method(path, int(line)), 3 if level == 'error' else 2))
    return results


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('tool', choices=['spotbugs', 'clang-tidy', 'ruff'])
    parser.add_argument('--executable')
    parser.add_argument('arguments', nargs=argparse.REMAINDER)
    args = parser.parse_args()
    extra = args.arguments[1:] if args.arguments[:1] == ['--'] else args.arguments
    executable = args.executable or args.tool
    if args.tool == 'spotbugs':
        with tempfile.TemporaryDirectory(prefix='cor-spotbugs-') as directory:
            output = Path(directory) / 'findings.xml'
            process = subprocess.run([executable, '-textui', '-xml:withMessages', '-output', str(output), *extra], capture_output=True, text=True)
            if process.returncode != 0 or not output.exists():
                raise RuntimeError(process.stderr or 'SpotBugs did not produce findings')
            findings = spotbugs(output.read_text(encoding='utf-8'))
    else:
        command = [executable, *(['check', '--output-format', 'json'] if args.tool == 'ruff' else []), *extra]
        process = subprocess.run(command, capture_output=True, text=True)
        if args.tool == 'ruff':
            if process.returncode not in (0, 1):
                raise RuntimeError(process.stderr)
            entries = json.loads(process.stdout)
            findings = [finding('ruff', f['code'], os.path.relpath(f['filename'], Path.cwd()), '<module>', 2) for f in entries]
        else:
            if process.returncode != 0:
                raise RuntimeError(process.stderr or process.stdout)
            findings = clang_findings(process.stdout + '\n' + process.stderr)
    print(json.dumps(findings))


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        print(f'Analyzer unavailable/failed: {error}', file=sys.stderr)
        sys.exit(2)
