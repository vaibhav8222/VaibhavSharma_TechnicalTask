using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using System.Collections.Generic;
using System.Configuration;

namespace GeneratorProcessingApp
{
    class Program
    {
        static void Main(string[] args)
        {
            // Load configuration
            string inputFolder = GetConfigValue("InputFolder");
            string outputFolder = GetConfigValue("OutputFolder");

            // Monitor input folder
            FileSystemWatcher watcher = new FileSystemWatcher(inputFolder, "*.xml");
            watcher.Created += (sender, e) => ProcessFile(e.FullPath, outputFolder);
            watcher.EnableRaisingEvents = true;

            Console.WriteLine("Monitoring input folder for XML files...");
            Console.ReadLine(); // Keep the app running
        }

        static void ProcessFile(string filePath, string outputFolder)
        {
            try
            {
                Console.WriteLine($"Processing file: {filePath}");
                // Parse input XML
                XDocument inputDoc = XDocument.Load(filePath);

                // Perform calculations
                var results = PerformCalculations(inputDoc);

                // Generate output XML
                string outputFileName = Path.Combine(outputFolder, Path.GetFileName(filePath).Replace(".xml", "-Result.xml"));
                results.Save(outputFileName);

                Console.WriteLine($"Output saved to: {outputFileName}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing file: {ex.Message}");
            }
        }

        static XDocument PerformCalculations(XDocument inputDoc)
        {
            // Load reference data
            var referenceData = GetReferenceData();

            // Extract all generator data (Wind, Gas, Coal) and their daily generation details
            var generators = inputDoc.Descendants("GenerationReport")
                .Elements() // Iterate over child elements (Wind, Gas, Coal)
                .Descendants("WindGenerator")
                .Union(inputDoc.Descendants("GenerationReport").Descendants("GasGenerator"))
                .Union(inputDoc.Descendants("GenerationReport").Descendants("CoalGenerator"))
                .SelectMany(x => x.Elements("Generation").Elements("Day") // Get each Day element within Generation
                    .Select(day => new
                    {
                        Name = (string)x.Element("Name"), // Generator Name
                        Type = x.Name.LocalName, // Type will be 'WindGenerator', 'GasGenerator', 'CoalGenerator'
                        Date = (DateTime)day.Element("Date"),
                        Energy = (decimal)day.Element("Energy"),
                        Price = (decimal)day.Element("Price"),
                        EmissionRating = (decimal?)x.Element("EmissionsRating"), // Only applicable for gas and coal
                        TotalHeatInput = (decimal?)x.Element("TotalHeatInput"), // Only applicable for coal
                        ActualNetGeneration = (decimal?)x.Element("ActualNetGeneration") // Only applicable for coal
                    }))
                .ToList();

            // Calculate Totals
            var totals = generators
                .GroupBy(g => g.Name)
                .Select(g => new XElement("Generator",
                    new XElement("Name", g.Key),
                    new XElement("Total", g.Sum(x => x.Energy * x.Price * GetValueFactor(referenceData, g.Key)))
                ))
                .ToList();

            // Calculate Max Emission Generators
            var maxEmissionGenerators = generators
                .Where(g => g.EmissionRating.HasValue)
                .GroupBy(g => g.Date)
                .Select(dayGroup => dayGroup.OrderByDescending(g =>
                    g.Energy * g.EmissionRating.Value * GetEmissionFactor(referenceData, g.Name)).First())
                .Select(maxGen => new XElement("Day",
                    new XElement("Name", maxGen.Name),
                    new XElement("Date", maxGen.Date.ToString("yyyy-MM-ddTHH:mm:sszzz")),
                    new XElement("Emission", maxGen.Energy * maxGen.EmissionRating.Value * GetEmissionFactor(referenceData, maxGen.Name))
                ))
                .ToList();

            // Calculate Actual Heat Rates
            var actualHeatRates = generators
                .Where(g => g.Type == "CoalGenerator" && g.TotalHeatInput.HasValue && g.ActualNetGeneration.HasValue)
                .GroupBy(g => g.Name)
                .Select(g => new XElement("ActualHeatRate",
                    new XElement("Name", g.Key),
                    new XElement("HeatRate", g.First().TotalHeatInput.Value / g.First().ActualNetGeneration.Value)
                ))
                .ToList();

            // Generate the final output XML document
            return new XDocument(
                new XElement("GenerationOutput",
                    new XElement("Totals", totals),
                    new XElement("MaxEmissionGenerators", maxEmissionGenerators),
                    new XElement("ActualHeatRates", actualHeatRates)
                )
            );
        }



        static object GetReferenceData()
        {
            // Load the reference data from the ReferenceData.xml file
            XDocument referenceDoc = XDocument.Load("ReferenceData.xml");
            var factors = referenceDoc.Descendants("Factors").First();

            var valueFactor = new
            {
                High = (decimal)factors.Element("ValueFactor").Element("High"),
                Medium = (decimal)factors.Element("ValueFactor").Element("Medium"),
                Low = (decimal)factors.Element("ValueFactor").Element("Low")
            };

            var emissionFactor = new
            {
                High = (decimal)factors.Element("EmissionsFactor").Element("High"),
                Medium = (decimal)factors.Element("EmissionsFactor").Element("Medium"),
                Low = (decimal)factors.Element("EmissionsFactor").Element("Low")
            };

            return new { ValueFactor = valueFactor, EmissionsFactor = emissionFactor };
        }

        static decimal GetValueFactor(dynamic referenceData, string name)
        {
            switch (name)
            {
                case "Wind[Offshore]": return referenceData.ValueFactor.Low;
                case "Wind[Onshore]": return referenceData.ValueFactor.High;
                case "Gas[1]": return referenceData.ValueFactor.Medium;
                case "Coal[1]": return referenceData.ValueFactor.Medium;
                default: return 0;
            }
        }

        static decimal GetEmissionFactor(dynamic referenceData, string name)
        {
            switch (name)
            {
                case "Gas[1]": return referenceData.EmissionsFactor.Medium;
                case "Coal[1]": return referenceData.EmissionsFactor.High;
                default: return 0;
            }
        }

        static string GetConfigValue(string key)
        {
            // Retrieve value from configuration file
            return ConfigurationManager.AppSettings[key];
        }
    }
}
