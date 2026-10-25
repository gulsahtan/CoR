"""Select disjoint new-study samples from a catalog of actual locally prepared defects."""
import argparse
import copy
import hashlib
import json
from pathlib import Path


def quotas(text):
    result = {part.split('=')[0]: int(part.split('=')[1]) for part in text.split(',')}
    if any(n < 0 for n in result.values()): raise ValueError('Negative quota')
    return result


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('catalog'); p.add_argument('output_directory'); p.add_argument('--seed', required=True)
    p.add_argument('--evaluation', default='Defects4J=150,QuixBugs=30,IntroClass=150')
    p.add_argument('--calibration', default='Defects4J=10,QuixBugs=10,IntroClass=10')
    a=p.parse_args(); path=Path(a.catalog).resolve(); catalog=json.loads(path.read_text(encoding='utf-8-sig'))
    evaluation=quotas(a.evaluation); calibration=quotas(a.calibration); cases=catalog['cases']; seen=set()
    for case in cases:
        key=(case['benchmark'],case['bugId'])
        if key in seen: raise ValueError('Duplicate catalog defect: '+str(key))
        seen.add(key)
        if not case.get('revision') or 'REPLACE' in case['revision']: raise ValueError('Pin every real checkout revision')
        for field in ['projectDirectory','evaluationDirectory','referenceMethodsFile','bugReportFile','stackTraceFile','historyFile']:
            if case.get(field): case[field]=str((path.parent/case[field]).resolve())
        for source in case['sourceFiles']:
            root=Path(case['projectDirectory']).resolve(); candidate=(root/source).resolve()
            if not candidate.is_relative_to(root) or not candidate.is_file(): raise ValueError('Missing/unsafe source: '+str(candidate))
    selected={s:[] for s in ['calibration','evaluation']}
    for benchmark in sorted(set(evaluation)|set(calibration)):
        ordered=sorted((c for c in cases if c['benchmark']==benchmark), key=lambda c:hashlib.sha256((a.seed+'|'+benchmark+'|'+c['bugId']).encode()).hexdigest())
        nc=calibration.get(benchmark,0); ne=evaluation.get(benchmark,0)
        if len(ordered)<nc+ne: raise ValueError(f'{benchmark}: need {nc+ne} distinct defects, found {len(ordered)}')
        for split, subset in [('calibration',ordered[:nc]),('evaluation',ordered[nc:nc+ne])]:
            selected[split].extend(dict(c,split=split) for c in subset)
    output=Path(a.output_directory)
    if output.exists(): raise ValueError('Use a new output directory to preserve previous manifests')
    output.mkdir(parents=True)
    for split in selected:
        manifest=copy.deepcopy(catalog); manifest['cases']=selected[split]; manifest['configurations']=[{'name':'full-cor'}]
        (output/(split+'.json')).write_text(json.dumps(manifest,indent=2),encoding='utf-8')
    (output/'selection.json').write_text(json.dumps(dict(protocol='new study, not original-paper replication',seed=a.seed,
        catalog_sha256=hashlib.sha256(path.read_bytes()).hexdigest(),evaluation=evaluation,calibration=calibration,
        selected={s:[c['benchmark']+'/'+c['bugId'] for c in rows] for s,rows in selected.items()}),indent=2),encoding='utf-8')

if __name__=='__main__': main()
