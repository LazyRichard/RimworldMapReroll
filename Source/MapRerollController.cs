﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;

namespace MapReroll {
	public class MapRerollController {
		public enum MapRerollType {
			Map, Geyser
		}

		public static readonly string ModName = "Map Reroll";

		private static MapRerollController instance;
		public static MapRerollController Instance {
			get {
				return instance ?? (instance = new MapRerollController());
			}
		}

		private const string LoadingMessageKey = "GeneratingMap";
		private const string CustomLoadingMessageKeyBase = "MapReroll_loading";

		// feel free to use these to detect reroll events 
		public event Action OnMapRerolled;
		public event Action OnGeysersRerolled;

		public MapRerollDef SettingsDef { get; private set; }

		public float ResourcePercentageRemaining { get; private set; }

		public bool ShowInterface {
			get { return Current.ProgramState == ProgramState.MapPlaying && SettingsDef != null && SettingsDef.enableInterface && Current.Game != null && Current.Game.Map != null && capturedInitData != null && !Faction.OfPlayer.HasName; }
		}

		private FieldInfo thingPrivateStateField;
		private FieldInfo genstepScattererProtectedUsedSpots;
		private FieldInfo factionManagerAllFactions;

		private bool mapRerollTriggered;
		private string originalWorldSeed;
		private GameInitData capturedInitData;
		private string stockLoadingMessage;

		public void Notify_OnLevelLoaded() {
			SettingsDef = DefDatabase<MapRerollDef>.GetNamed("mapRerollSettings", false);
			PrepareReflectionReferences();
			if(SettingsDef == null) {
				Log.Error("[MapReroll] Settings Def was not loaded.");
				return;
			}

			// reset Genstep_Scatterer interal state
			ResetScattererGensteps();

			if(mapRerollTriggered) {
				ReduceMapResources(100-(ResourcePercentageRemaining), 100);
				SubtractResourcePercentage(SettingsDef.mapRerollCost);
				mapRerollTriggered = false;
				Find.World.info.seedString = originalWorldSeed;
				KillIntroDialog();
				RestoreVanillaLoadingMessage();
				if(OnMapRerolled!=null) OnMapRerolled();
			} else {
				ResourcePercentageRemaining = 100f;
				originalWorldSeed = Find.World.info.seedString;
			}
		}

		public void RerollMap() {
			if(mapRerollTriggered) return;
			mapRerollTriggered = true;
			//Action preLoadLevelAction = delegate {
			var pawns = GetAllColonistsOnMap();
			foreach (var pawn in pawns) {
				if (pawn.IsColonist) {
					// colonist might still be in pod
					if (pawn.Spawned) {
						pawn.ClearMind();
						pawn.ClearReservations();
						pawn.health.Reset();
						pawn.DeSpawn();
					}
					// clear relation with bonded pet
					var bondedPet = pawn.relations.GetFirstDirectRelationPawn(PawnRelationDefOf.Bond);
					if(bondedPet != null) {
						pawn.relations.RemoveDirectRelation(PawnRelationDefOf.Bond, bondedPet);
					}
				}
			}

			Find.Selector.SelectedObjects.Clear();

			// discard the old player faction so that scenarios can do their thing
			DiscardWorldSquareFactions(capturedInitData.startingCoords);

			var sameWorld = Current.Game.World;
			var sameScenario = Current.Game.Scenario;
			var sameStoryteller = Current.Game.storyteller;

			Current.ProgramState = ProgramState.Entry;
			Current.Game = new Game();
			var newInitData = Current.Game.InitData = new GameInitData();
			Current.Game.Scenario = sameScenario;
			Find.Scenario.PreConfigure();
			newInitData.permadeath = capturedInitData.permadeath;
			newInitData.startingCoords = capturedInitData.startingCoords;
			newInitData.startingMonth = capturedInitData.startingMonth;
			newInitData.mapSize = capturedInitData.mapSize;

			Current.Game.World = sameWorld;
			Current.Game.storyteller = sameStoryteller;
			sameWorld.info.seedString = Rand.Int.ToString();

			Find.Scenario.PostWorldLoad();
			
			MapIniter_NewGame.PrepForMapGen();
			Find.Scenario.PreMapGenerate();

			StartingPawnUtility.ClearAllStartingPawns();

			newInitData.startingPawns = capturedInitData.startingPawns;
			foreach (var startingPawn in newInitData.startingPawns) {
				startingPawn.SetFactionDirect(newInitData.playerFaction);
			}

			SetCustomLoadingMessage();
			LongEventHandler.QueueLongEvent(() => { }, "Map", "GeneratingMap", true, null);
			
		}

		private void SetCustomLoadingMessage() {
			var customLoadingMessage = stockLoadingMessage = LoadingMessageKey.Translate();

			if(SettingsDef.useSillyLoadingMessages) {
				var messageIndex = Rand.Range(0, SettingsDef.numLoadingMessages - 1);
				var messageKey = CustomLoadingMessageKeyBase + messageIndex;
				if (messageKey.CanTranslate()) {
					customLoadingMessage = messageKey.Translate();
				}
			}

			LanguageDatabase.activeLanguage.keyedReplacements[LoadingMessageKey] = customLoadingMessage;
		}

		private void RestoreVanillaLoadingMessage() {
			LanguageDatabase.activeLanguage.keyedReplacements[LoadingMessageKey] = stockLoadingMessage;
		}

		private void PrepareReflectionReferences() {
			thingPrivateStateField = typeof(Thing).GetField("thingStateInt", BindingFlags.Instance | BindingFlags.NonPublic);
			genstepScattererProtectedUsedSpots = typeof(GenStep_Scatterer).GetField("usedSpots", BindingFlags.Instance | BindingFlags.NonPublic);
			factionManagerAllFactions = typeof(FactionManager).GetField("allFactions", BindingFlags.Instance | BindingFlags.NonPublic);
			if(thingPrivateStateField == null || genstepScattererProtectedUsedSpots == null || factionManagerAllFactions == null) {
				Log.Error("Failed to get named fields by reflection");
			}
		}

		private void DiscardWorldSquareFactions(IntVec2 square) {
			var factionList = (List<Faction>)factionManagerAllFactions.GetValue(Find.FactionManager);
			Faction faction;
			while ((faction = Find.FactionManager.FactionInWorldSquare(square)) != null) {
				faction.RemoveAllRelations();
				factionList.Remove(faction);	
			}
		}

		public void RerollGeysers() {
			var geyserGen = TryGetGeyserGenstep();
			if (geyserGen != null) {
				TryGenerateGeysersWithNewLocations(geyserGen);
				SubtractResourcePercentage(SettingsDef.geyserRerollCost);
				if(OnGeysersRerolled!=null) OnGeysersRerolled();
			} else {
				Log.Error("Failed to find the Genstep for geysers. Check your map generator config.");
			}
		}

		public bool CanAffordOperation(MapRerollType type) {
			float cost = 0;
			switch (type) {
				case MapRerollType.Map: cost = SettingsDef.mapRerollCost; break;
				case MapRerollType.Geyser: cost = SettingsDef.geyserRerollCost; break;
			}
			return ResourcePercentageRemaining >= cost;
		}

		internal void SetCapturedInitData(GameInitData initData) {
			capturedInitData = initData;
		}

		// get all colonists, including those still in drop pods
		private IEnumerable<Pawn> GetAllColonistsOnMap() {
			return Find.MapPawns.PawnsInFaction(Faction.OfPlayer).Where(p => p.IsColonist).ToList();
		}

		// Genstep_Scatterer instances build up internal state during generation
		// if not reset, after enough rerolls, the map generator will fail to find spots to place geysers, items, resources, etc.
		private void ResetScattererGensteps() {
			var mapGenDef = DefDatabase<MapGeneratorDef>.AllDefs.FirstOrDefault();
			if (mapGenDef == null) return;
			foreach (var genStepDef in mapGenDef.GenStepsInOrder) {
				var genstepScatterer = genStepDef.genStep as GenStep_Scatterer;
				if (genstepScatterer != null) {
					ResetScattererGenstepInternalState(genstepScatterer);		
				}
			}
		}

		private void ResetScattererGenstepInternalState(GenStep_Scatterer genstep) {
			// field is protected, use reflection
			var usedSpots = (HashSet<IntVec3>) genstepScattererProtectedUsedSpots.GetValue(genstep);
			if(usedSpots!=null) {
				usedSpots.Clear();
			}
		}

		// Genstep_ScatterThings is prone to generating things in the same spot on occasion.
		// If that happens we try to run it a few more times to try and get new positions.
		private void TryGenerateGeysersWithNewLocations(GenStep_ScatterThings geyserGen) {
			const int MaxGeyserGenerationAttempts = 10;
			var collisionsDetected = true;
			var attemptsRemaining = MaxGeyserGenerationAttempts;
			while (attemptsRemaining>0 && collisionsDetected) {
				var usedSpots = new HashSet<IntVec3>(GetAllGeyserPositionsOnMap());
				// destroy existing geysers
				Thing.allowDestroyNonDestroyable = true;
				Find.ListerThings.ThingsOfDef(ThingDefOf.SteamGeyser).ForEach(t => t.Destroy());
				Thing.allowDestroyNonDestroyable = false;
				// make new geysers
				geyserGen.Generate();
				// clean up internal state
				ResetScattererGenstepInternalState(geyserGen);
				// check if some geysers were generated in the same spots
				collisionsDetected = false;
				foreach (var geyserPos in GetAllGeyserPositionsOnMap()) {
					if(usedSpots.Contains(geyserPos)) {
						collisionsDetected = true;
					}
				}
				attemptsRemaining--;
			}
		}

		private IEnumerable<IntVec3> GetAllGeyserPositionsOnMap() {
			return Find.ListerThings.ThingsOfDef(ThingDefOf.SteamGeyser).Select(t => t.Position);
		}

		private void ReduceMapResources(float consumePercent, float currentResourcesAtPercent) {
			if (currentResourcesAtPercent == 0) return;
			var allResourceDefs = DefDatabase<ThingDef>.AllDefs.Where(def => def.building != null && def.building.mineableScatterCommonality > 0).ToArray();
			var rockDef = Find.World.NaturalRockTypesIn(Find.Map.WorldCoords).FirstOrDefault();
			var mapResources = Find.ListerThings.AllThings.Where(t => allResourceDefs.Contains(t.def)).ToList();

			var newResourceAmount = Mathf.Clamp(currentResourcesAtPercent - consumePercent, 0, 100);
			var originalResAmount = Mathf.Ceil(mapResources.Count / (currentResourcesAtPercent/100));
			var percentageChange = currentResourcesAtPercent - newResourceAmount;
			var resourceToll = (int)Mathf.Ceil(Mathf.Abs(originalResAmount * (percentageChange/100)));

			var toll = resourceToll;
			if (mapResources.Count > 0) {
				// eat random resources
				while (mapResources.Count > 0 && toll > 0) {
					var resIndex = UnityEngine.Random.Range(0, mapResources.Count);
					var resThing = mapResources[resIndex];

					SneakilyDestroyResource(resThing);
					mapResources.RemoveAt(resIndex);
					// put some rock in their place
					if (rockDef != null) {
						var rock = ThingMaker.MakeThing(rockDef);
						GenPlace.TryPlaceThing(rock, resThing.Position, ThingPlaceMode.Direct);
					}
					toll--;
				}
			}
			if (!SettingsDef.logConsumedResources) return;
			Log.Message("[MapReroll] Ordered to consume " + consumePercent + "%, with current resources at " + currentResourcesAtPercent + "%. Consuming " + resourceToll + " resource spots, " + mapResources.Count + " left");
			if (toll > 0) Log.Message("[MapReroll] Failed to consume " + toll + " resource spots.");
		}

		private void SubtractResourcePercentage(float percent) {
			ReduceMapResources(percent, ResourcePercentageRemaining);
			ResourcePercentageRemaining = Mathf.Clamp(ResourcePercentageRemaining - percent, 0, 100);
		}

		// destroying a resource outright causes too much overhead: fog, area reveal, pathing, roof updates, etc
		// we just want to replace it. So, we just despawn it and do some cleanup.
		// As of A13 despawning triggers all of the above. So, we do all the cleanup manually.
		// This approach may break with future releases (if thing despawning changes), so it's worth checking over.
		// The following is Thing.Despawn code with the unnecessary parts stripped out, plus key parts from Building.Despawn
		private void SneakilyDestroyResource(Thing res) {
			Find.Map.listerThings.Remove(res);
			Find.ThingGrid.Deregister(res);
			Find.CoverGrid.DeRegister(res);
			if (res.def.hasTooltip) {
				Find.TooltipGiverList.DeregisterTooltipGiver(res);
			}
			if (res.def.graphicData != null && res.def.graphicData.Linked) {
				LinkGrid.Notify_LinkerCreatedOrDestroyed(res);
				Find.MapDrawer.MapMeshDirty(res.Position, MapMeshFlag.Things, true, false);
			}
			Find.Selector.Deselect(res);
			if (res.def.drawerType != DrawerType.RealtimeOnly) {
				var cellRect = res.OccupiedRect();
				for (var i = cellRect.minZ; i <= cellRect.maxZ; i++) {
					for (var j = cellRect.minX; j <= cellRect.maxX; j++) {
						Find.Map.mapDrawer.MapMeshDirty(new IntVec3(j, 0, i), MapMeshFlag.Things);
					}
				}
			}
			if (res.def.drawerType != DrawerType.MapMeshOnly) {
				Find.DynamicDrawManager.DeRegisterDrawable(res);
			}
			thingPrivateStateField.SetValue(res, res.def.DiscardOnDestroyed ? ThingState.Discarded : ThingState.Memory);
			Find.TickManager.DeRegisterAllTickabilityFor(res);
			Find.AttackTargetsCache.Notify_ThingDespawned(res);
			// building-specific cleanup 
			Find.ListerBuildings.Remove((Building) res);
			Find.DesignationManager.RemoveAllDesignationsOn(res);
		}

		private GenStep_ScatterThings TryGetGeyserGenstep() {
			var mapGenDef = DefDatabase<MapGeneratorDef>.AllDefs.FirstOrDefault();
			if (mapGenDef == null) return null;
			return (GenStep_ScatterThings)mapGenDef.GenStepsInOrder.Find(g => {
				var gen = g.genStep as GenStep_ScatterThings;
				return gen != null && gen.thingDef == ThingDefOf.SteamGeyser;
			}).genStep;
		}

		private void KillIntroDialog(){
			Find.WindowStack.TryRemove(typeof(Dialog_NodeTree), false);
		}
	}
}