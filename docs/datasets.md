# Dataset Sources

This replication package is designed to run benchmark-style instances derived from established program repair datasets. Always cite the dataset authors and follow their licenses.

## Defects4J

- Official repository: https://github.com/rjust/defects4j
- Project site: https://defects4j.org/

## QuixBugs

- Official repository: https://github.com/jkoppel/QuixBugs

## IntroClass

- Public repository: https://github.com/ProgramRepair/IntroClass

## Expected Case Conversion

Each benchmark defect can be converted to the CLI layout:

```text
case-id/
  source.java | source.py | source.c | source.cs
  failing.txt
  bug.txt
```

The framework records dataset provenance through JSON logs and CLI output files.
