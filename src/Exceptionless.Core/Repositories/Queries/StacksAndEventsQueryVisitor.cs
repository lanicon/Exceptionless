﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Exceptionless.Core.Extensions;
using Exceptionless.Core.Repositories.Configuration;
using Foundatio.Parsers.LuceneQueries;
using Foundatio.Parsers.LuceneQueries.Extensions;
using Foundatio.Parsers.LuceneQueries.Nodes;
using Foundatio.Parsers.LuceneQueries.Visitors;

namespace Exceptionless.Core.Repositories.Queries {
    public class StacksAndEventsQueryVisitor : ChainableQueryVisitor {
        private readonly ISet<string> _stackOnlyFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            StackIndex.Alias.LastOccurrence, "last_occurrence",
            StackIndex.Alias.References, "references",
            "status",
            "snooze_until_utc",
            StackIndex.Alias.SignatureHash, "signature_hash",
            "title",
            "description",
            "first_occurrence",
            StackIndex.Alias.DateFixed, "date_fixed",
            StackIndex.Alias.FixedInVersion, "fixed_in_version",
            StackIndex.Alias.OccurrencesAreCritical, "occurrences_are_critical",
            StackIndex.Alias.TotalOccurrences, "total_occurrences"
        };

        private readonly ISet<string> _stackOnlySpecialFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            StackIndex.Alias.IsFixed, "is_fixed",
            StackIndex.Alias.IsRegressed, "is_regressed",
            StackIndex.Alias.IsHidden, "is_hidden"
        };

        private readonly ISet<string> _stackNonInvertedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "organization_id", StackIndex.Alias.OrganizationId,
            "project_id", StackIndex.Alias.ProjectId,
            EventIndex.Alias.StackId, "stack_id",
            StackIndex.Alias.Type,
        };

        private readonly ISet<string> _stackAndEventFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "organization_id", StackIndex.Alias.OrganizationId,
            "project_id", StackIndex.Alias.ProjectId,
            EventIndex.Alias.StackId, "stack_id",
            StackIndex.Alias.Type,
            StackIndex.Alias.Tags, "tags"
        };

        private readonly ISet<string> _stackFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public StacksAndEventsQueryVisitor(StacksAndEventsQueryMode queryMode) {
            _stackFields.AddRange(_stackOnlyFields);
            _stackFields.AddRange(_stackAndEventFields);

            QueryMode = queryMode;
        }

        public StacksAndEventsQueryMode QueryMode { get; set; } = StacksAndEventsQueryMode.Events;
        public bool IsInvertSuccessful { get; set; } = true;
        public bool HasStatusOpen { get; set; } = false;

        public override Task VisitAsync(GroupNode node, IQueryVisitorContext context) {
            ApplyFilter(node, context);

            return base.VisitAsync(node, context);
        }

        public override async Task VisitAsync(TermNode node, IQueryVisitorContext context) {
            var filteredNode = ApplyFilter(node, context);
            if (filteredNode is GroupNode newGroupNode) {
                await base.VisitAsync(newGroupNode, context);
                return;
            }
            
            if (String.Equals(node.Field, "status", StringComparison.OrdinalIgnoreCase)
                && !node.IsNegated.GetValueOrDefault()
                && String.Equals(node.Term, "open", StringComparison.OrdinalIgnoreCase))
                HasStatusOpen = true;

            if (QueryMode != StacksAndEventsQueryMode.InvertedStacks)
                return;

            if (_stackNonInvertedFields.Contains(filteredNode.Field))
                return;

            var groupNode = node.GetGroupNode();

            // went to root node, just negate current node
            if (!groupNode.HasParens) {
                node.IsNegated = node.IsNegated.HasValue ? !node.IsNegated : true;
                return;
            }

            // check to see if we already inverted the group
            if (groupNode.Data.ContainsKey("@IsInverted"))
                return;

            var referencedFields = await GetReferencedFieldsQueryVisitor.RunAsync(groupNode, context);
            if (referencedFields.Any(f => _stackNonInvertedFields.Contains(f))) {
                // if we have referenced fields that are on the list of non-inverted fields and the operator is an OR then its an issue, mark invert unsuccessful
                if (node.GetOperator(context) == GroupOperator.Or) {
                    IsInvertSuccessful = false;
                    return;
                }

                node.IsNegated = node.IsNegated.HasValue ? !node.IsNegated : true;
                return;
            }

            // negate the entire group
            if (groupNode.Left != null && groupNode.Right != null)
                groupNode.IsNegated = groupNode.IsNegated.HasValue ? !groupNode.IsNegated : true;
            groupNode.Data["@IsInverted"] = true;
        }

        public override void Visit(TermRangeNode node, IQueryVisitorContext context) {
            ApplyFilter(node, context);
        }

        public override void Visit(ExistsNode node, IQueryVisitorContext context) {
            ApplyFilter(node, context);
        }

        public override void Visit(MissingNode node, IQueryVisitorContext context) {
            ApplyFilter(node, context);
        }

        private IFieldQueryNode ApplyFilter(IFieldQueryNode node, IQueryVisitorContext context) {
            if (node.Field == null)
                return node;

            var parent = node.Parent as GroupNode;

            if (QueryMode == StacksAndEventsQueryMode.Stacks || QueryMode == StacksAndEventsQueryMode.InvertedStacks) {
                if (_stackFields.Contains(node.Field))
                    return node;

                // check for special field names
                if (node is TermNode termNode) {
                    switch (node.Field?.ToLowerInvariant()) {
                        case EventIndex.Alias.StackId:
                        case "stack_id":
                            termNode.Field = "id";
                            return node;

                        case "is_fixed":
                        case StackIndex.Alias.IsFixed:
                            bool isFixed = Boolean.TryParse(termNode.Term, out bool temp) && temp;
                            termNode.Field = "status";
                            termNode.Term = "fixed";
                            termNode.IsNegated = !isFixed;
                            return node;

                        case "is_regressed":
                        case StackIndex.Alias.IsRegressed:
                            bool isRegressed = Boolean.TryParse(termNode.Term, out bool regressed) && regressed;
                            termNode.Field = "status";
                            termNode.Term = "regressed";
                            termNode.IsNegated = !isRegressed;
                            return node;

                        case "is_hidden":
                        case StackIndex.Alias.IsHidden:
                            if (parent == null)
                                break;

                            bool isHidden = Boolean.TryParse(termNode.Term, out bool hidden) && hidden;
                            if (isHidden) {
                                var isHiddenNode = new GroupNode {
                                    HasParens = true,
                                    IsNegated = true,
                                    Operator = GroupOperator.And,
                                    Left = new TermNode { Field = "status", Term = "open" },
                                    Right = new TermNode { Field = "status", Term = "regressed" }
                                };
                                if (parent.Left == node)
                                    parent.Left = isHiddenNode;
                                else if (parent.Right == node)
                                    parent.Right = isHiddenNode;

                                return isHiddenNode;
                            } else {
                                var notHiddenNode = new GroupNode {
                                    HasParens = true,
                                    Operator = GroupOperator.Or,
                                    Left = new TermNode { Field = "status", Term = "open" },
                                    Right = new TermNode { Field = "status", Term = "regressed" }
                                };

                                if (parent.Left == node)
                                    parent.Left = notHiddenNode;
                                else if (parent.Right == node)
                                    parent.Right = notHiddenNode;

                                return notHiddenNode;
                            }
                    }
                }

                if (parent == null)
                    return node;

                if (parent.Left == node)
                    parent.Left = null;
                else if (parent.Right == node)
                    parent.Right = null;
            } else {
                if (_stackOnlyFields.Contains(node.Field) || _stackOnlySpecialFields.Contains(node.Field)) {
                    // remove criteria that is only for stacks

                    if (parent == null)
                        return node;

                    if (parent.Left == node)
                        parent.Left = null;
                    else if (parent.Right == node)
                        parent.Right = null;
                }
            }

            return node;
        }

        public override async Task<IQueryNode> AcceptAsync(IQueryNode node, IQueryVisitorContext context) {
            await node.AcceptAsync(this, context).AnyContext();
            return node;
        }

        public static async Task<StacksAndEventsQueryResult> RunAsync(IQueryNode node, StacksAndEventsQueryMode queryMode, IQueryVisitorContext context = null) {
            var visitor = new StacksAndEventsQueryVisitor(queryMode);
            var stackNode = await visitor.AcceptAsync(node, context).AnyContext();
            var result = await GenerateQueryVisitor.RunAsync(stackNode, context).AnyContext();

            return new StacksAndEventsQueryResult {
                Query = result,
                IsInvertSuccessful = visitor.IsInvertSuccessful
            };
        }

        public static async Task<StacksAndEventsQueryResult> RunAsync(string query, StacksAndEventsQueryMode queryMode, IQueryVisitorContext context = null) {
            var parser = new LuceneQueryParser();
            var result = await parser.ParseAsync(query, context).AnyContext();
            return await RunAsync(result, queryMode, context).AnyContext();
        }

        public static StacksAndEventsQueryResult Run(IQueryNode node, StacksAndEventsQueryMode queryMode, IQueryVisitorContext context = null) {
            return RunAsync(node, queryMode, context).GetAwaiter().GetResult();
        }

        public static StacksAndEventsQueryResult Run(string query, StacksAndEventsQueryMode queryMode, IQueryVisitorContext context = null) {
            return RunAsync(query, queryMode, context).GetAwaiter().GetResult();
        }
    }

    public class StacksAndEventsQueryResult {
        public string Query { get; set; }
        public bool IsInvertSuccessful { get; set; }
        public bool HasStatusOpen { get; set; }
    }

    public enum StacksAndEventsQueryMode {
        Stacks,
        InvertedStacks,
        Events
    }
}