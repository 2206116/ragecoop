
using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using GTA;
using GTA.Math;
using GTA.Native;
using HarmonyLib;
using RageCoop.Client.Scripting;

namespace RageCoop.Resources.SyncFix
{
    public class Main : ClientScript
    {


        public override void OnStart()
        {

        }

        public override void OnStop()
        {

        }

        // Per-tick: hard-set heading for remote, on-foot, non-aiming peds
    }   
}
