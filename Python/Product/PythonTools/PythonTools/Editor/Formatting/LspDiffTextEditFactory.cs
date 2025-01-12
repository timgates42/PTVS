﻿// Python Tools for Visual Studio
// Copyright(c) Microsoft Corporation
// All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the License); you may not use
// this file except in compliance with the License. You may obtain a copy of the
// License at http://www.apache.org/licenses/LICENSE-2.0
//
// THIS CODE IS PROVIDED ON AN  *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS
// OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY
// IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE,
// MERCHANTABILITY OR NON-INFRINGEMENT.
//
// See the Apache Version 2.0 License for specific language governing
// permissions and limitations under the License.

using System;
using System.Collections.Generic;
using System.Linq;
using DiffMatchPatch;
using Microsoft.PythonTools.Infrastructure;
using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace Microsoft.PythonTools.Editor.Formatting {
    internal static class LspDiffTextEditFactory {
        private enum Action { Unknown, Delete, Insert, Replace };

        /// <summary>
        /// Converts output from a command-line diff tool into LSP compatible text edits.
        /// </summary>
        public static TextEdit[] GetEdits(string documentText, string diffOutputText) {
            if (documentText == null) {
                throw new ArgumentNullException(nameof(documentText));
            }

            if (diffOutputText == null) {
                throw new ArgumentNullException(nameof(diffOutputText));
            }

            // Diff tools may print a header with the original/modified file names
            // and that header must be removed for diff_match_patch.
            diffOutputText = diffOutputText.Trim(new char[] { '\uFEFF'});
            if (diffOutputText.StartsWithOrdinal("---")) {
                var startIndex = diffOutputText.IndexOfOrdinal("@@");
                if (startIndex >= 0) {
                    diffOutputText = diffOutputText.Substring(startIndex);
                }

                // TODO: needed?
                // Remove the text added by unified_diff
                // # Work around missing newline (http://bugs.python.org/issue2142).
                //patch = patch.replace(/\\ No newline at end of file[\r\n] /, '');
            }

            var patches = new diff_match_patch().patch_fromText(diffOutputText);

            var edits = patches.SelectMany(p => GetEdits(documentText, p));
            return edits.ToArray();
        }

        private static IEnumerable<TextEdit> GetEdits(string before, Patch patch) {
            var line = patch.start1;
            var diffs = patch.diffs;
            int character = 0;
            if (line > 0) {
                var beforeLines = before
                    .Split(new[] { '\r', '\n' }, line + 1, StringSplitOptions.None)
                    .Take(Math.Max(0, line - 1));

                foreach (var beforeLine in beforeLines) {
                    character += beforeLine.Length + Environment.NewLine.Length;
                }
            }

            TextEdit edit = null;
            var action = Action.Unknown;

            var start = new Position(line, character);

            for (int i = 0; i < diffs.Count; i++) {
                switch (diffs[i].operation) {
                    case Operation.DELETE:
                        if (edit == null) {
                            edit = new TextEdit() {
                                NewText = null,
                                Range = new Range() {
                                    Start = start,
                                },
                            };
                            action = Action.Delete;
                        } else if (action == Action.Insert || action == Action.Replace) {
                            throw new FormatException("patch parsing error");
                        }
                        edit.Range.End = new Position(line, diffs[i].text.Length);
                        break;

                    case Operation.INSERT:
                        if (edit == null) {
                            edit = new TextEdit() {
                                Range = new Range() {
                                    Start = start,
                                }
                            };
                            action = Action.Insert;
                        } else if (action == Action.Delete) {
                            action = Action.Replace;
                        }

                        // insert and replace edits are all relative to the original state
                        // of the document, so inserts should reset the current line/character
                        // position to the start.
                        line = start.Line;

                        //only add newline before text if not inserting at begining of file
                        bool isPatchStart = (line == patch.start1) && (edit.Range.Start.Character == 0);
                        if (!isPatchStart) {
                            edit.NewText += Environment.NewLine;
                        }

                        edit.NewText += diffs[i].text;
                        break;

                    case Operation.EQUAL:
                        if (edit != null) {
                            if (edit.Range.End == null) {
                                edit.Range.End = action == Action.Insert
                                    ? edit.Range.Start
                                    : new Position(line, character);
                            }

                            yield return edit;

                            edit = null;
                            action = Action.Unknown;
                        }
                        break;

                    default:
                        break;
                }

                // Find the end of the current line and append the next change to this position
                // because the editor wont allow you to add lines that are outside the original document.
                character = diffs[i].text.Length;
                start = new Position(line, character);
                line++;
            }

            if (edit != null) {
                if (edit.Range.End == null) {
                    edit.Range.End = action == Action.Insert
                        ? edit.Range.Start
                        : new Position(line - 1, character); //readjust line to account for final ++
                }

                yield return edit;
            }
        }
    }
}
