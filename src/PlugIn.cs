﻿// This file is part of SHE for LANDIS-II.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Landis.Core;
using Landis.Extension.SOSIELHarvest.Algorithm;
using Landis.Extension.SOSIELHarvest.Configuration;
using Landis.Extension.SOSIELHarvest.Helpers;
using Landis.Extension.SOSIELHarvest.Input;
using Landis.Extension.SOSIELHarvest.Models;
using Landis.Library.BiomassCohorts;
using Landis.Library.HarvestManagement;
using Landis.SpatialModeling;
using Landis.Utilities;

namespace Landis.Extension.SOSIELHarvest
{
    public class PlugIn : ExtensionMain
    {
        public static readonly ExtensionType ExtType = new ExtensionType("disturbance:harvest");

        public static readonly string ExtensionName = "SOSIEL Harvest";

        private SheParameters _sheParameters;
        private SosielParameters _sosielParameters;
        private BiomassHarvest.Parameters _biomassHarvestParameters;

        private ConfigurationModel _configuration;

        private AlgorithmModel sosielHarvestModel;
        private SosielHarvestImplementation sosielHarvest;
        private BiomassHarvest.PlugIn _biomassHarvest;
        private IManagementAreaDataset _biomassHarvestAreas;
        private List<ExtendedPrescription> _extendedPrescriptions;


        private static ICore modelCore;


        private Dictionary<Area, double> projectedBiomass;

        private int iteration = 1;


        //---------------------------------------------------------------------

        public PlugIn()
            : base(ExtensionName, ExtType)
        {
            _extendedPrescriptions = new List<ExtendedPrescription>();
        }

        //---------------------------------------------------------------------

        public static ICore ModelCore => modelCore;
        //---------------------------------------------------------------------

        public override void LoadParameters(string dataFile, ICore mCore)
        {
#if DEBUG
            Debugger.Launch();
#endif
            modelCore = mCore;

            ModelCore.UI.WriteLine("  Loading parameters from {0}", dataFile);
            var sheParameterParser = new SheParameterParser();
            _sheParameters = Data.Load(dataFile, sheParameterParser);

            ModelCore.UI.WriteLine("  Loading parameters from {0}", _sheParameters.SosielInitializationFileName);
            var sosielParameterParser = new SosielParameterParser();
            _sosielParameters = Data.Load(_sheParameters.SosielInitializationFileName, sosielParameterParser);

            if (_sheParameters.Mode == 2)
            {
                ModelCore.UI.WriteLine("  Loading parameters from {0}",
                    _sheParameters.BiomassHarvestInitializationFileName);
                _biomassHarvest = new BiomassHarvest.PlugIn();
                _biomassHarvest.LoadParameters(_sheParameters.BiomassHarvestInitializationFileName, ModelCore);
            }
        }

        //---------------------------------------------------------------------

        public override void Initialize()
        {
            ModelCore.UI.WriteLine("Initializing {0}...", Name);
            SiteVars.Initialize();

            Timestep = _sheParameters.Timestep;

            _configuration = ConfigurationParser.MakeConfiguration(_sosielParameters);

            ////create algorithm instance
            int iterations = 1; // Later we can decide if there should be multiple SHE sub-iterations per LANDIS-II iteration. 
            ////create dictionary 
            projectedBiomass = new Dictionary<Area, double>();

            sosielHarvestModel = new AlgorithmModel();
            var managementAreas = _sheParameters.AgentToManagementAreaList.GroupBy(m => m.ManagementArea)
                .Select(g => new Area() {Name = g.Key, AssignedAgents = g.Select(m => m.Agent).ToArray()})
                .ToArray();
            sosielHarvest = new SosielHarvestImplementation(iterations, _configuration, managementAreas, projectedBiomass);

            sosielHarvest.Initialize(sosielHarvestModel);


            ////remove old output files
            //System.IO.DirectoryInfo di = new System.IO.DirectoryInfo(System.IO.Directory.GetCurrentDirectory());

            //foreach (System.IO.FileInfo fi in di.GetFiles("SOSIELHuman_*.csv"))
            //{
            //    fi.Delete();
            //}

            if (_sheParameters.Mode == 2)
            {
                _biomassHarvest.Initialize();

                var biomassHarvestPluginType = _biomassHarvest.GetType();
                var prescriptionField = biomassHarvestPluginType.GetField("managementAreas",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (prescriptionField == null)
                    throw new Exception();
                _biomassHarvestAreas = (IManagementAreaDataset)prescriptionField.GetValue(_biomassHarvest);

                var parametersField =
                    biomassHarvestPluginType.GetField("parameters", BindingFlags.Static | BindingFlags.NonPublic);
                if (parametersField == null)
                    throw new Exception();
                _biomassHarvestParameters = (BiomassHarvest.Parameters)parametersField.GetValue(_biomassHarvest);
            }
        }

        public override void Run()
        {
            //run SOSIEL algorithm
            var model = sosielHarvest.Run(sosielHarvestModel);


            //use model.
            if (iteration == 1)
            {
                GenerateNewPrescription("newPrescription", "HarvestArea", 0.5, "MM1-1_DO1");
                ApplyNewPrescription("newPrescription");
            }

            if (_sheParameters.Mode == 2)
                _biomassHarvest.Run();

            iteration++;
        }

        private void GenerateNewPrescription(string newName, string parameter, dynamic value, string basedOn)
        {
            var originalPrescription = _biomassHarvestParameters.Prescriptions.First(p => p.Name.Equals(basedOn));

            var newPrescription = originalPrescription.Copy();

            var type = newPrescription.GetType();
            var nameField = type.GetField("name", BindingFlags.Instance | BindingFlags.NonPublic);
            nameField.SetValue(newPrescription, newName);

            var rankingMethodField = type.GetField("rankingMethod", BindingFlags.Instance | BindingFlags.NonPublic);
            rankingMethodField.SetValue(newPrescription, originalPrescription.StandRankingMethod);

            var cohortCutterField = type.GetField("cohortCutter", BindingFlags.Instance | BindingFlags.NonPublic);
            var cohortCutter = cohortCutterField.GetValue(originalPrescription);
            cohortCutterField.SetValue(newPrescription, cohortCutter);

            AppliedPrescription appliedPrescription = null;

            foreach (var managementArea in _biomassHarvestParameters.ManagementAreas)
            {
                appliedPrescription =
                    managementArea.Prescriptions.FirstOrDefault(p => p.Prescription.Name.Equals(basedOn));

                if (appliedPrescription != null)
                    break;
            }

            var areaToHarvest = appliedPrescription.PercentageToHarvest;
            var standsToHarvest = appliedPrescription.PercentStandsToHarvest;
            var beginTime = appliedPrescription.BeginTime;
            var endTime = appliedPrescription.EndTime;

            switch (parameter)
            {
                case "HarvestArea":
                    areaToHarvest = new Percentage(value);
                    break;
                case "CohortsRemovedProportionAdjustment":

                    break;
            }

            _extendedPrescriptions.Add(new ExtendedPrescription(newPrescription, areaToHarvest, standsToHarvest,
                beginTime, endTime));
        }

        private void ApplyNewPrescription(string prescriptionName)
        {
            foreach (var biomassHarvestArea in _biomassHarvestAreas)
            {
                biomassHarvestArea.Prescriptions.Clear();
                var extendedPrescription =
                    _extendedPrescriptions.First(p => p.Prescription.Name.Equals(prescriptionName));
                biomassHarvestArea.ApplyPrescription(extendedPrescription.Prescription,
                    new Percentage(extendedPrescription.HarvestAreaPercent),
                    new Percentage(extendedPrescription.HarvestStandsAreaPercent), extendedPrescription.StartTime,
                    extendedPrescription.EndTime);
                biomassHarvestArea.FinishInitialization();
            }
        }

        //---------------------------------------------------------------------

        private static double ComputeSiteBiomass(ActiveSite site)
        {
            double siteBiomass = 0;
            if (SiteVars.Cohorts[site] != null)
                foreach (ISpeciesCohorts speciesCohorts in SiteVars.Cohorts[site])
                    foreach (ICohort cohort in speciesCohorts)
                        siteBiomass += cohort.Biomass;
            return siteBiomass;
        }
    }
}