"""Offline helper regression fixtures are not benchmark measurements."""
import csv
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]


def module(name):
    spec = importlib.util.spec_from_file_location(name, ROOT/'scripts'/f'{name}.py')
    result = importlib.util.module_from_spec(spec); spec.loader.exec_module(result)
    return result


class Helpers(unittest.TestCase):
    def test_spotbugs_severity_and_identity(self):
        result=module('static_analysis').spotbugs('<BugCollection><BugInstance type="NP_NULL" priority="1"><Method classname="C" name="m" signature="()V"/><SourceLine sourcepath="C.java"/></BugInstance></BugCollection>')
        self.assertEqual(result[0]['severity'],3); self.assertEqual(result[0]['method'],'C.m()V'); self.assertEqual(result[0]['category'],'null-safety')

    def test_spotbugs_internal_error_is_not_clean(self):
        with self.assertRaises(ValueError): module('static_analysis').spotbugs('<BugCollection><Errors errors="1"/></BugCollection>')

    def test_calibration_grid(self):
        configs=module('experiment_matrix').configurations('calibration')
        self.assertEqual(len(configs),9); self.assertIn(10,[c.get('topK') for c in configs])
        self.assertEqual(len({c['name'] for c in configs}),len(configs))

    def test_unknown_oracle_status(self):
        p=subprocess.run([sys.executable,str(ROOT/'scripts/oracle.py'),'command','--',sys.executable,'-c','import sys;sys.exit(5)'],capture_output=True)
        self.assertEqual(p.returncode,2)

    def test_failing_test_is_not_success(self):
        p=subprocess.run([sys.executable,str(ROOT/'scripts/oracle.py'),'command','--',sys.executable,'-c','import sys;sys.exit(1)'],capture_output=True)
        self.assertEqual(p.returncode,1)

    def test_defects4j_exit_zero_with_failures_is_failed(self):
        p=subprocess.run([sys.executable,str(ROOT/'scripts/oracle.py'),'defects4j','--',sys.executable,'-c',"print('Failing tests: 2')"],capture_output=True)
        self.assertEqual(p.returncode,1)

    def test_missing_analyzer_is_error(self):
        p=subprocess.run([sys.executable,str(ROOT/'scripts/static_analysis.py'),'--executable','cor-missing-tool','ruff','--','.'],capture_output=True)
        self.assertEqual(p.returncode,2); self.assertNotEqual(p.stdout.strip(),b'[]')

    def test_new_study_sampling_is_disjoint_and_reproducible(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp); (root/'source.c').write_text('int main(){return 0;}')
            cases=[dict(benchmark='IntroClass',bugId=f'offline-fixture-{i}',revision='fixture-not-benchmark',projectDirectory=str(root),sourceFiles=['source.c']) for i in range(4)]
            catalog=root/'catalog.json'; catalog.write_text(json.dumps(dict(schemaVersion=1,cases=cases)))
            for name in ['a','b']:
                p=subprocess.run([sys.executable,str(ROOT/'scripts/select_sample.py'),str(catalog),str(root/name),'--seed','offline-test','--evaluation','IntroClass=2','--calibration','IntroClass=2'],capture_output=True)
                self.assertEqual(p.returncode,0,p.stderr)
            sa=json.loads((root/'a/selection.json').read_text()); sb=json.loads((root/'b/selection.json').read_text())
            self.assertEqual(sa,sb); self.assertFalse(set(sa['selected']['evaluation']) & set(sa['selected']['calibration']))

    def test_insufficient_population_is_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp); (root/'catalog.json').write_text('{"cases":[]}')
            p=subprocess.run([sys.executable,str(ROOT/'scripts/select_sample.py'),str(root/'catalog.json'),str(root/'out'),'--seed','test'],capture_output=True)
            self.assertNotEqual(p.returncode,0); self.assertFalse((root/'out').exists())

if __name__=='__main__': unittest.main()
