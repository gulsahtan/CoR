"""Expand a real case manifest into prespecified evaluation/calibration configurations."""
import argparse
import copy
import json
from pathlib import Path


def configurations(suite):
    def config(name, **kw):
        return dict(name=name, **kw)
    if suite == 'calibration':
        vectors = [('default', [.4, .3, .2, .1]), ('equal', [.25]*4), ('code-dominant', [.55, .2, .15, .1]),
                   ('trace-dominant', [.25, .45, .2, .1]), ('no-history', [.4/.9, .3/.9, .2/.9, 0])]
        return [config(name, weights=dict(zip(['sourceContext', 'failingOutput', 'bugReport', 'revisionHistory'], w))) for name, w in vectors] + [
            config('topk-3', topK=3), config('topk-10', topK=10), config('refinement-1', maxRefinements=1), config('refinement-5', maxRefinements=5)]
    return [config('full-cor'), config('direct', workflow='direct'), config('lexical', workflow='lexical'),
        config('without-validation', disabledValidationRules=['all']),
        config('code-only', useTrace=False, useReport=False, useHistory=False),
        config('code-trace', useReport=False, useHistory=False), config('code-trace-report', useHistory=False),
        config('without-source-ranking', weights=dict(sourceContext=0, failingOutput=.3, bugReport=.2, revisionHistory=.1)),
        config('without-trace', useTrace=False), config('without-report', useReport=False), config('without-history', useHistory=False),
        *[config('without-'+rule, disabledValidationRules=[rule]) for rule in ['null-safety', 'type-consistency', 'causal-alignment']]]


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('input'); p.add_argument('output'); p.add_argument('--suite', choices=['evaluation', 'calibration'], required=True)
    a = p.parse_args(); source = Path(a.input).resolve(); destination = Path(a.output).resolve()
    if destination.exists():
        raise ValueError('Output exists; choose a new manifest path')
    manifest = json.loads(source.read_text(encoding='utf-8-sig'))
    if not manifest.get('cases') or any(c.get('split', 'evaluation') != a.suite for c in manifest['cases']):
        raise ValueError('Provide real cases from the selected split; do not reuse evaluation IDs for calibration')
    for case in manifest['cases']:
        for key in ['projectDirectory', 'evaluationDirectory', 'referenceMethodsFile', 'bugReportFile', 'stackTraceFile', 'historyFile']:
            if case.get(key): case[key] = str((source.parent / case[key]).resolve())
    manifest['configurations'] = configurations(a.suite)
    destination.write_text(json.dumps(manifest, indent=2), encoding='utf-8')

if __name__ == '__main__':
    main()
