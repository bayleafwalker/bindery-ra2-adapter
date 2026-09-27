// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Playbooks;

/// <summary>
/// The on-disk / wire shape of a set of <see cref="Playbook"/>s, so an operator
/// can author more of them in JSON with <see cref="BotJson.Options"/> (the same
/// schema this package uses internally for the 12 default playbooks).
/// </summary>
public sealed record PlaybookDocument(IReadOnlyList<Playbook> Playbooks);
