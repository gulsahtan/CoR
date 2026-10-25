"""Retrospective oracle: 0=all passed, 1=tests failed, 2=infrastructure error."""
import argparse
import re
import subprocess
import sys


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('kind', choices=['defects4j', 'pytest', 'command'])
    parser.add_argument('command', nargs=argparse.REMAINDER)
    args = parser.parse_args()
    command = args.command[1:] if args.command[:1] == ['--'] else args.command
    if not command:
        raise ValueError('An explicit test command is required')
    process = subprocess.run(command, capture_output=True, text=True)
    print(process.stdout, end='')
    print(process.stderr, file=sys.stderr, end='')
    if args.kind == 'defects4j':
        matches = re.findall(r'^Failing tests:\s*(\d+)\s*$', process.stdout + '\n' + process.stderr, re.M)
        if not matches or process.returncode not in (0, 1):
            return 2
        return 0 if all(int(n) == 0 for n in matches) and process.returncode == 0 else 1
    return process.returncode if process.returncode in (0, 1) else 2


if __name__ == '__main__':
    try:
        sys.exit(main())
    except Exception as error:
        print(f'Oracle infrastructure failure: {error}', file=sys.stderr)
        sys.exit(2)
