# Architecture

WPF host -> secure configuration/services -> domain state -> Risk Gate -> execution -> SQLite audit -> WebView2 Runtime Bridge -> Web UI.

The Web UI must not store secrets or call exchanges directly. Exchange providers implement the common adapter contracts. Strategy research persists lifecycle state in SQLite and supplies approved local signals to the decision workflow.
