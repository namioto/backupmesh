# Third-Party Notices

BackupMesh distributions may bundle the following independent executables. They are not covered by the BackupMesh Apache License 2.0.

The pinned versions and archive checksums are recorded in [`tools/third-party-tools.json`](tools/third-party-tools.json).

## restic

- Project: [restic](https://github.com/restic/restic)
- Bundled version: 0.19.1
- Purpose: Phase 1 backup engine, executed by the Source Agent
- License: BSD 2-Clause License
- Modifications: None planned; official release binaries will be distributed unchanged
- License text: [`licenses/restic-BSD-2-Clause.txt`](licenses/restic-BSD-2-Clause.txt)

## rest-server

- Project: [rest-server](https://github.com/restic/rest-server)
- Bundled version: 0.14.0
- Purpose: Phase 1 REST backend, managed by the Storage Agent
- License: BSD 2-Clause License
- Modifications: None planned; official release binaries will be distributed unchanged
- License text: [`licenses/rest-server-BSD-2-Clause.txt`](licenses/rest-server-BSD-2-Clause.txt)

## Release requirements

Every BackupMesh release that bundles third-party software must:

1. Pin and record the exact component versions and checksums in its release manifest.
2. Include this notice and the corresponding license texts in source and binary distributions.
3. Preserve upstream copyright, license, and disclaimer notices.
4. Review the complete transitive license inventory whenever a component is built from source or modified.
5. Clearly identify any upstream modifications.
