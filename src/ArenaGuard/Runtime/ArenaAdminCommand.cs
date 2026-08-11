using System;
using System.Collections.Generic;
using System.Linq;
using ArenaGuard.World;
using Jotunn.Entities;

namespace ArenaGuard.Runtime
{
    internal sealed class ArenaAdminCommand : ConsoleCommand
    {
        public override string Name => "arenaguard";
        public override string Help =>
            "SkaldHall recovery: arenaguard status [arena] | abort <arena|all> | clearqueue <arena|all>";
        public override bool IsCheat => false;
        // ArenaGuard carries remote requests through its own authenticated RPC so the server
        // always derives administrator identity from the actual routed sender.
        public override bool IsNetwork => false;
        public override bool OnlyServer => false;

        public override List<string> CommandOptionList()
        {
            return new List<string> { "status", "abort", "clearqueue" };
        }

        public override void Run(string[] args, Terminal context)
        {
            bool dedicatedConsole = ZNet.instance != null && ZNet.instance.IsServer() &&
                                    Player.m_localPlayer == null;
            if (!dedicatedConsole && !ArenaWorldObjects.IsLocalAdmin())
            {
                context?.AddString("SkaldHall: only an authenticated server administrator may use this command.");
                return;
            }

            if (args == null || args.Length == 0)
            {
                context?.AddString("Usage: " + Help);
                return;
            }

            string operation = (args[0] ?? string.Empty).Trim().ToLowerInvariant();
            string target = args.Length <= 1
                ? string.Empty
                : string.Join(" ", args.Skip(1).ToArray()).Trim();
            if (!ArenaServerRuntime.RequestAdminCommand(operation, target, out string response))
            {
                context?.AddString("SkaldHall: " + response);
                return;
            }

            context?.AddString("SkaldHall: " + response);
        }
    }
}
