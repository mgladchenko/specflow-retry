﻿using System;
using System.CodeDom;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TechTalk.SpecFlow;
using TechTalk.SpecFlow.Generator;
using TechTalk.SpecFlow.Generator.Configuration;
using TechTalk.SpecFlow.Generator.UnitTestConverter;
using TechTalk.SpecFlow.Generator.UnitTestProvider;
using TechTalk.SpecFlow.Parser.SyntaxElements;
using TechTalk.SpecFlow.Tracing;
using TechTalk.SpecFlow.Utils;

namespace SpecFlow.Retry
{
    public class RetryUnitTestFeatureGenerator : IFeatureGenerator
    {
        private const string DEFAULT_NAMESPACE = "SpecFlowTests";
        const string TESTCLASS_NAME_FORMAT = "{0}Feature";
        const string TEST_NAME_FORMAT = "{0}";
        private const string IGNORE_TAG = "Ignore";

        private const string SCENARIO_INITIALIZE_NAME = "ScenarioSetup";
        private const string SCENARIO_CLEANUP_NAME = "ScenarioCleanup";
        private const string TEST_INITIALIZE_NAME = "TestInitialize";
        private const string TEST_CLEANUP_NAME = "ScenarioTearDown";
        private const string TESTCLASS_INITIALIZE_NAME = "FeatureSetup";
        private const string TESTCLASS_CLEANUP_NAME = "FeatureTearDown";
        private const string BACKGROUND_NAME = "FeatureBackground";
        private const string TESTRUNNER_FIELD = "testRunner";
        private const string SPECFLOW_NAMESPACE = "TechTalk.SpecFlow";
        private const string SCENARIO_OUTLINE_EXAMPLE_TAGS_PARAMETER = "exampleTags";


        private readonly IUnitTestGeneratorProvider testGeneratorProvider;
        private readonly CodeDomHelper codeDomHelper;
        private readonly GeneratorConfiguration generatorConfiguration;
        private readonly IDecoratorRegistry decoratorRegistry;
        private readonly ITagFilterMatcher tagFilterMatcher;

        public RetryUnitTestFeatureGenerator(IUnitTestGeneratorProvider testGeneratorProvider, CodeDomHelper codeDomHelper, GeneratorConfiguration generatorConfiguration, IDecoratorRegistry decoratorRegistry, ITagFilterMatcher tagFilterMatcher)
        {
            this.testGeneratorProvider = testGeneratorProvider;
            this.codeDomHelper = codeDomHelper;
            this.generatorConfiguration = generatorConfiguration;
            this.decoratorRegistry = decoratorRegistry;
            this.tagFilterMatcher = tagFilterMatcher;
        }

        private CodeMemberMethod CreateMethod(CodeTypeDeclaration type)
        {
            CodeMemberMethod method = new CodeMemberMethod();
            type.Members.Add(method);
            return method;
        }

        private static bool HasFeatureBackground(Feature feature)
        {
            return feature.Background != null;
        }

        private TestClassGenerationContext CreateTestClassStructure(CodeNamespace codeNamespace, string testClassName, Feature feature)
        {
            var testClass = codeDomHelper.CreateGeneratedTypeDeclaration(testClassName);
            codeNamespace.Types.Add(testClass);

            return new TestClassGenerationContext(
                testGeneratorProvider,
                feature,
                codeNamespace,
                testClass,
                CreateMethod(testClass),
                CreateMethod(testClass),
                CreateMethod(testClass),
                CreateMethod(testClass),
                CreateMethod(testClass),
                CreateMethod(testClass),
                HasFeatureBackground(feature) ? CreateMethod(testClass) : null,
                generateRowTests: testGeneratorProvider.SupportsRowTests && generatorConfiguration.AllowRowTests,
                generateAsynchTests: generatorConfiguration.GenerateAsyncTests && testGeneratorProvider.SupportsAsyncTests);
        }

        private CodeNamespace CreateNamespace(string targetNamespace)
        {
            targetNamespace = targetNamespace ?? DEFAULT_NAMESPACE;

            CodeNamespace codeNamespace = new CodeNamespace(targetNamespace);

            codeNamespace.Imports.Add(new CodeNamespaceImport(SPECFLOW_NAMESPACE));
            return codeNamespace;
        }

        public CodeNamespace GenerateUnitTestFixture(Feature feature, string testClassName, string targetNamespace)
        {
            CodeNamespace codeNamespace = CreateNamespace(targetNamespace);

            testClassName = testClassName ?? string.Format(TESTCLASS_NAME_FORMAT, feature.Title.ToIdentifier());
            var generationContext = CreateTestClassStructure(codeNamespace, testClassName, feature);

            SetupTestClass(generationContext);
            SetupTestClassInitializeMethod(generationContext);
            SetupTestClassCleanupMethod(generationContext);

            SetupScenarioInitializeMethod(generationContext);
            SetupFeatureBackground(generationContext);
            SetupScenarioCleanupMethod(generationContext);

            SetupTestInitializeMethod(generationContext);
            SetupTestCleanupMethod(generationContext);


            foreach (var scenario in feature.Scenarios)
            {
                if (string.IsNullOrEmpty(scenario.Title))
                    throw new TestGeneratorException("The scenario must have a title specified.");

                var scenarioOutline = scenario as ScenarioOutline;
                if (scenarioOutline != null)
                    GenerateScenarioOutlineTest(generationContext, scenarioOutline);
                else
                    GenerateTest(generationContext, scenario);
            }

            //before return the generated code, call generate provider's method in case the provider want to customerize the generated code            
            testGeneratorProvider.FinalizeTestClass(generationContext);
            return codeNamespace;
        }

        private void SetupScenarioCleanupMethod(TestClassGenerationContext generationContext)
        {
            CodeMemberMethod scenarioCleanupMethod = generationContext.ScenarioCleanupMethod;

            scenarioCleanupMethod.Attributes = MemberAttributes.Public;
            scenarioCleanupMethod.Name = SCENARIO_CLEANUP_NAME;

            // call collect errors
            var testRunnerField = GetTestRunnerExpression();
            //testRunner.CollectScenarioErrors();
            scenarioCleanupMethod.Statements.Add(
                new CodeMethodInvokeExpression(
                    testRunnerField,
                    "CollectScenarioErrors"));
        }

        private void SetupTestClass(TestClassGenerationContext generationContext)
        {
            generationContext.TestClass.IsPartial = true;
            generationContext.TestClass.TypeAttributes |= TypeAttributes.Public;

            AddLinePragmaInitial(generationContext.TestClass, generationContext.Feature.SourceFile);

            testGeneratorProvider.SetTestClass(generationContext, generationContext.Feature.Title, generationContext.Feature.Description);

            List<string> featureCategories;
            decoratorRegistry.DecorateTestClass(generationContext, out featureCategories);

            if (featureCategories.Any())
                testGeneratorProvider.SetTestClassCategories(generationContext, featureCategories);

            DeclareTestRunnerMember(generationContext);
        }

        private void DeclareTestRunnerMember(TestClassGenerationContext generationContext)
        {
            CodeMemberField testRunnerField = new CodeMemberField(typeof(ITestRunner), TESTRUNNER_FIELD);
            testRunnerField.Attributes |= MemberAttributes.Static;
            generationContext.TestClass.Members.Add(testRunnerField);
        }

        private CodeExpression GetTestRunnerExpression()
        {
            return new CodeVariableReferenceExpression(TESTRUNNER_FIELD);
        }

        private IEnumerable<string> GetNonIgnoreTags(IEnumerable<Tag> tags)
        {
            if (tags == null)
                return new string[0];
            return tags.Where(t => !t.Name.Equals(IGNORE_TAG, StringComparison.InvariantCultureIgnoreCase)).Select(t => t.Name);
        }

        private bool HasIgnoreTag(IEnumerable<Tag> tags)
        {
            if (tags == null)
                return false;
            return tags.Any(t => t.Name.Equals(IGNORE_TAG, StringComparison.InvariantCultureIgnoreCase));
        }

        private void SetupTestClassInitializeMethod(TestClassGenerationContext generationContext)
        {
            var testClassInitializeMethod = generationContext.TestClassInitializeMethod;

            testClassInitializeMethod.Attributes = MemberAttributes.Public;
            testClassInitializeMethod.Name = TESTCLASS_INITIALIZE_NAME;

            testGeneratorProvider.SetTestClassInitializeMethod(generationContext);

            //testRunner = TestRunnerManager.GetTestRunner();
            var testRunnerField = GetTestRunnerExpression();
            var methodName = generationContext.GenerateAsynchTests ? "GetAsyncTestRunner" : "GetTestRunner";
            testClassInitializeMethod.Statements.Add(
                new CodeAssignStatement(
                    testRunnerField,
                    new CodeMethodInvokeExpression(
                        new CodeTypeReferenceExpression(typeof(TestRunnerManager)),
                        methodName)));

            //FeatureInfo featureInfo = new FeatureInfo("xxxx");
            testClassInitializeMethod.Statements.Add(
                new CodeVariableDeclarationStatement(typeof(FeatureInfo), "featureInfo",
                    new CodeObjectCreateExpression(typeof(FeatureInfo),
                        new CodeObjectCreateExpression(typeof(CultureInfo),
                            new CodePrimitiveExpression(generationContext.Feature.Language)),
                        new CodePrimitiveExpression(generationContext.Feature.Title),
                        new CodePrimitiveExpression(generationContext.Feature.Description),
                        new CodeFieldReferenceExpression(
                            new CodeTypeReferenceExpression("ProgrammingLanguage"),
                            codeDomHelper.TargetLanguage.ToString()),
                        GetStringArrayExpression(generationContext.Feature.Tags))));

            //testRunner.OnFeatureStart(featureInfo);
            testClassInitializeMethod.Statements.Add(
                new CodeMethodInvokeExpression(
                    testRunnerField,
                    "OnFeatureStart",
                    new CodeVariableReferenceExpression("featureInfo")));
        }

        private CodeExpression GetStringArrayExpression(Tags tags)
        {
            if (tags == null || tags.Count == 0)
                return new CodeCastExpression(typeof(string[]), new CodePrimitiveExpression(null));

            return new CodeArrayCreateExpression(typeof(string[]), tags.Select(tag => new CodePrimitiveExpression(tag.Name)).Cast<CodeExpression>().ToArray());
        }

        private CodeExpression GetStringArrayExpression(IEnumerable<string> items, ParameterSubstitution paramToIdentifier)
        {
            return new CodeArrayCreateExpression(typeof(string[]), items.Select(item => GetSubstitutedString(item, paramToIdentifier)).ToArray());
        }

        private void SetupTestClassCleanupMethod(TestClassGenerationContext generationContext)
        {
            CodeMemberMethod testClassCleanupMethod = generationContext.TestClassCleanupMethod;

            testClassCleanupMethod.Attributes = MemberAttributes.Public;
            testClassCleanupMethod.Name = TESTCLASS_CLEANUP_NAME;

            testGeneratorProvider.SetTestClassCleanupMethod(generationContext);

            var testRunnerField = GetTestRunnerExpression();
            //            testRunner.OnFeatureEnd();
            testClassCleanupMethod.Statements.Add(
                new CodeMethodInvokeExpression(
                    testRunnerField,
                    "OnFeatureEnd"));
            //            testRunner = null;
            testClassCleanupMethod.Statements.Add(
                new CodeAssignStatement(
                    testRunnerField,
                    new CodePrimitiveExpression(null)));
        }

        private void SetupTestInitializeMethod(TestClassGenerationContext generationContext)
        {
            CodeMemberMethod testInitializeMethod = generationContext.TestInitializeMethod;

            testInitializeMethod.Attributes = MemberAttributes.Public;
            testInitializeMethod.Name = TEST_INITIALIZE_NAME;

            testGeneratorProvider.SetTestInitializeMethod(generationContext);
        }

        private void SetupTestCleanupMethod(TestClassGenerationContext generationContext)
        {
            CodeMemberMethod testCleanupMethod = generationContext.TestCleanupMethod;

            testCleanupMethod.Attributes = MemberAttributes.Public;
            testCleanupMethod.Name = TEST_CLEANUP_NAME;

            testGeneratorProvider.SetTestCleanupMethod(generationContext);

            var testRunnerField = GetTestRunnerExpression();
            //testRunner.OnScenarioEnd();
            testCleanupMethod.Statements.Add(
                new CodeMethodInvokeExpression(
                    testRunnerField,
                    "OnScenarioEnd"));
        }

        private void SetupScenarioInitializeMethod(TestClassGenerationContext generationContext)
        {
            CodeMemberMethod scenarioInitializeMethod = generationContext.ScenarioInitializeMethod;

            scenarioInitializeMethod.Attributes = MemberAttributes.Public;
            scenarioInitializeMethod.Name = SCENARIO_INITIALIZE_NAME;
            scenarioInitializeMethod.Parameters.Add(
                new CodeParameterDeclarationExpression(typeof(ScenarioInfo), "scenarioInfo"));

            //testRunner.OnScenarioStart(scenarioInfo);
            var testRunnerField = GetTestRunnerExpression();
            scenarioInitializeMethod.Statements.Add(
                new CodeMethodInvokeExpression(
                    testRunnerField,
                    "OnScenarioStart",
                    new CodeVariableReferenceExpression("scenarioInfo")));
        }

        private void SetupFeatureBackground(TestClassGenerationContext generationContext)
        {
            if (!HasFeatureBackground(generationContext.Feature))
                return;

            var background = generationContext.Feature.Background;

            CodeMemberMethod backgroundMethod = generationContext.FeatureBackgroundMethod;

            backgroundMethod.Attributes = MemberAttributes.Public;
            backgroundMethod.Name = BACKGROUND_NAME;

            AddLineDirective(backgroundMethod.Statements, background);

            foreach (var given in background.Steps)
                GenerateStep(backgroundMethod, given, null);

            AddLineDirectiveHidden(backgroundMethod.Statements);
        }

        private class ParameterSubstitution : List<KeyValuePair<string, string>>
        {
            public void Add(string parameter, string identifier)
            {
                Add(new KeyValuePair<string, string>(parameter.Trim(), identifier));
            }

            public bool TryGetIdentifier(string param, out string id)
            {
                param = param.Trim();
                foreach (var pair in this)
                {
                    if (pair.Key.Equals(param))
                    {
                        id = pair.Value;
                        return true;
                    }
                }
                id = null;
                return false;
            }
        }

        private void GenerateScenarioOutlineTest(TestClassGenerationContext generationContext, ScenarioOutline scenarioOutline)
        {
            ValidateExampleSetConsistency(scenarioOutline);

            ParameterSubstitution paramToIdentifier = CreateParamToIdentifierMapping(scenarioOutline);

            var scenatioOutlineTestMethod = CreateScenatioOutlineTestMethod(generationContext, scenarioOutline, paramToIdentifier);
            var exampleTagsParam = new CodeVariableReferenceExpression(SCENARIO_OUTLINE_EXAMPLE_TAGS_PARAMETER);

            if (generationContext.GenerateRowTests)
            {
                GenerateScenarioOutlineExamplesAsRowTests(generationContext, scenarioOutline, scenatioOutlineTestMethod);
            }
            else
            {
                GenerateScenarioOutlineExamplesAsIndividualMethods(scenarioOutline, generationContext, scenatioOutlineTestMethod, paramToIdentifier);
            }

            int retryValue;
            if (GetTagValue(generationContext, scenarioOutline, TagsRepository.RETRY_TAG, Int32.TryParse, out retryValue))
            {
                string retryExceptExceptionName;

                GetTagValue(generationContext, scenarioOutline, TagsRepository.RETRY_EXCEPT_TAG, out retryExceptExceptionName);

                scenatioOutlineTestMethod = GenerateRetryStatementAndUnwrap(generationContext, scenatioOutlineTestMethod, retryValue, retryExceptExceptionName);
            }

            GenerateTestBody(generationContext, scenarioOutline, scenatioOutlineTestMethod, exampleTagsParam, paramToIdentifier);
        }

        private void GenerateScenarioOutlineExamplesAsIndividualMethods(ScenarioOutline scenarioOutline, TestClassGenerationContext generationContext, CodeMemberMethod scenatioOutlineTestMethod, ParameterSubstitution paramToIdentifier)
        {
            int exampleSetIndex = 0;
            foreach (var exampleSet in scenarioOutline.Examples.ExampleSets)
            {
                bool useFirstColumnAsName = CanUseFirstColumnAsName(exampleSet.Table);
                string exampleSetIdentifier = string.IsNullOrEmpty(exampleSet.Title)
                                                  ? scenarioOutline.Examples.ExampleSets.Count(es => string.IsNullOrEmpty(es.Title)) > 1
                                                        ? string.Format("ExampleSet {0}", exampleSetIndex).ToIdentifier()
                                                        : null
                                                  : exampleSet.Title.ToIdentifier();

                for (int rowIndex = 0; rowIndex < exampleSet.Table.Body.Length; rowIndex++)
                {
                    var row = exampleSet.Table.Body[rowIndex];

                    string variantName = useFirstColumnAsName ? row.Cells[0].Value : string.Format("Variant {0}", rowIndex);
                    GenerateScenarioOutlineTestVariant(generationContext, scenarioOutline, scenatioOutlineTestMethod, paramToIdentifier, exampleSet.Title ?? "", exampleSetIdentifier, row, exampleSet.Tags, variantName);
                }
                exampleSetIndex++;
            }
        }

        private void GenerateScenarioOutlineExamplesAsRowTests(TestClassGenerationContext generationContext, ScenarioOutline scenarioOutline, CodeMemberMethod scenatioOutlineTestMethod)
        {
            SetupTestMethod(generationContext, scenatioOutlineTestMethod, scenarioOutline, null, rowTest: true);

            foreach (var exampleSet in scenarioOutline.Examples.ExampleSets)
            {
                foreach (var row in exampleSet.Table.Body)
                {
                    var arguments = row.Cells.Select(c => c.Value);
                    testGeneratorProvider.SetRow(generationContext, scenatioOutlineTestMethod, arguments, GetNonIgnoreTags(exampleSet.Tags), HasIgnoreTag(exampleSet.Tags));
                }
            }
        }

        private ParameterSubstitution CreateParamToIdentifierMapping(ScenarioOutline scenarioOutline)
        {
            ParameterSubstitution paramToIdentifier = new ParameterSubstitution();
            foreach (var param in scenarioOutline.Examples.ExampleSets[0].Table.Header.Cells)
                paramToIdentifier.Add(param.Value, param.Value.ToIdentifierCamelCase());
            return paramToIdentifier;
        }

        private void ValidateExampleSetConsistency(ScenarioOutline scenarioOutline)
        {
            if (scenarioOutline.Examples.ExampleSets.Length <= 1)
                return;

            var firstExampleSetHeader =
                scenarioOutline.Examples.ExampleSets[0].Table.Header.Cells.Select(c => c.Value).ToArray();

            //check params
            if (scenarioOutline.Examples.ExampleSets.Skip(1)
                .Select(exampleSet => exampleSet.Table.Header.Cells.Select(c => c.Value))
                .Any(paramNames => !paramNames.SequenceEqual(firstExampleSetHeader)))
            {
                throw new TestGeneratorException("The example sets must provide the same parameters.");
            }
        }

        private bool CanUseFirstColumnAsName(GherkinTable table)
        {
            if (table.Header.Cells.Length == 0)
                return false;

            return table.Body.Select(r => r.Cells[0].Value.ToIdentifier()).Distinct().Count() == table.Body.Length;
        }

        private CodeMemberMethod CreateScenatioOutlineTestMethod(TestClassGenerationContext generationContext, ScenarioOutline scenarioOutline, ParameterSubstitution paramToIdentifier)
        {
            CodeMemberMethod testMethod = CreateMethod(generationContext.TestClass);

            testMethod.Attributes = MemberAttributes.Public;
            testMethod.Name = string.Format(TEST_NAME_FORMAT, scenarioOutline.Title.ToIdentifier());

            foreach (var pair in paramToIdentifier)
            {
                testMethod.Parameters.Add(new CodeParameterDeclarationExpression(typeof(string), pair.Value));
            }

            testMethod.Parameters.Add(new CodeParameterDeclarationExpression(typeof(string[]), SCENARIO_OUTLINE_EXAMPLE_TAGS_PARAMETER));
            return testMethod;
        }

        private void GenerateScenarioOutlineTestVariant(TestClassGenerationContext generationContext, ScenarioOutline scenarioOutline, CodeMemberMethod scenatioOutlineTestMethod,
            IEnumerable<KeyValuePair<string, string>> paramToIdentifier, string exampleSetTitle, string exampleSetIdentifier,
            GherkinTableRow row, Tags exampleSetTags, string variantName)
        {
            var variantNameIdentifier = variantName.ToIdentifier().TrimStart('_');

            CodeMemberMethod testMethod = CreateTestMethod(generationContext, scenarioOutline, exampleSetTags);
            testMethod.Name = string.IsNullOrEmpty(exampleSetIdentifier)
                ? string.Format("{0}_{1}", testMethod.Name, variantNameIdentifier)
                : string.Format("{0}_{1}_{2}", testMethod.Name, exampleSetIdentifier, variantNameIdentifier);

            //call test implementation with the params
            List<CodeExpression> argumentExpressions = row.Cells.Select(paramCell => new CodePrimitiveExpression(paramCell.Value)).Cast<CodeExpression>().ToList();

            argumentExpressions.Add(GetStringArrayExpression(exampleSetTags));

            testMethod.Statements.Add(
                new CodeMethodInvokeExpression(
                    new CodeThisReferenceExpression(),
                    scenatioOutlineTestMethod.Name,
                    argumentExpressions.ToArray()));

            var arguments = paramToIdentifier.Select((p2i, paramIndex) => new KeyValuePair<string, string>(p2i.Key, row.Cells[paramIndex].Value)).ToList();
            testGeneratorProvider.SetTestMethodAsRow(generationContext, testMethod, scenarioOutline.Title, exampleSetTitle, variantName, arguments);
        }

        private void GenerateTest(TestClassGenerationContext generationContext, Scenario scenario)
        {
            CodeMemberMethod testMethod = CreateTestMethod(generationContext, scenario, null);

            int retryValue;
            if (GetTagValue(generationContext, scenario, TagsRepository.RETRY_TAG, Int32.TryParse, out retryValue))
            {
                string retryExceptExceptionName;

                GetTagValue(generationContext, scenario, TagsRepository.RETRY_EXCEPT_TAG, out retryExceptExceptionName);

                testMethod = GenerateRetryStatementAndUnwrap(generationContext, testMethod, retryValue, retryExceptExceptionName);
            }

            GenerateTestBody(generationContext, scenario, testMethod);
        }

        private delegate bool TryParseDelegate<T>(string value, out T parsedValue);

        private bool GetTagValue(TestClassGenerationContext generationContext, Scenario scenario, string retryTag, out string value)
        {
            return GetTagValue(generationContext, scenario, retryTag, delegate(string v, out string p) { p = v; return v != null; }, out value);
        }

        private bool GetTagValue<T>(TestClassGenerationContext generationContext, Scenario scenario, string retryTag, TryParseDelegate<T> parser, out T value)
        {
            value = default(T);
            var tagNames = scenario.Tags != null ? scenario.Tags.Select(_ => _.Name) : new string[0];
            string retryCountValue;
            return tagFilterMatcher.GetTagValue(retryTag, tagNames, out retryCountValue)
                        && parser(retryCountValue, out value) ||
                   tagFilterMatcher.GetTagValue(retryTag, generationContext.Feature, out retryCountValue)
                        && parser(retryCountValue, out value);
        }


        private CodeMemberMethod GenerateRetryStatementAndUnwrap(TestClassGenerationContext generationContext, CodeMemberMethod testMethod, int retryCount, string retryExceptExceptionName)
        {
            var method = CreateMethod(generationContext.TestClass);

            method.Name = testMethod.Name + "Internal";

            foreach (CodeParameterDeclarationExpression parameter in testMethod.Parameters)
            {
                method.Parameters.Add(new CodeParameterDeclarationExpression(parameter.Type, parameter.Name));
            }

            testMethod.Statements.Add(new CodeVariableDeclarationStatement(typeof (Exception), "lastException", new CodePrimitiveExpression(null)));

            var codeCatchClauses = new List<CodeCatchClause>();

            if (!String.IsNullOrEmpty(retryExceptExceptionName))
            {
                codeCatchClauses.Add(new CodeCatchClause(
                    "exc",
                    new CodeTypeReference(new CodeTypeParameter(retryExceptExceptionName)),
                        new CodeThrowExceptionStatement()
                    ));
            }

            codeCatchClauses.Add(new CodeCatchClause(
                "exc",
                new CodeTypeReference(typeof(Exception)),
                /* lastException = exc; */
                new CodeAssignStatement(
                    new CodeVariableReferenceExpression("lastException"),
                    new CodeVariableReferenceExpression("exc"))));

            testMethod.Statements.Add(
                new CodeIterationStatement(
                    /* for(var i = 0; i < retryCount; i++)*/
                    initStatement:
                        new CodeVariableDeclarationStatement(typeof (int), "i", new CodePrimitiveExpression(0)),
                    testExpression: new CodeBinaryOperatorExpression(
                        new CodeVariableReferenceExpression("i"),
                        CodeBinaryOperatorType.LessThanOrEqual,
                        new CodePrimitiveExpression(retryCount)
                        ),
                    incrementStatement: new CodeAssignStatement(
                        new CodeVariableReferenceExpression("i"),
                        new CodeBinaryOperatorExpression(
                            new CodeVariableReferenceExpression("i"),
                            CodeBinaryOperatorType.Add,
                            new CodePrimitiveExpression(1))),
                    statements: new CodeStatement[]
                    {
                        /* try */
                        new CodeTryCatchFinallyStatement(
                            tryStatements: new CodeStatement[]
                            {
                                /* { */
                                /*  this.TestInternal(); */
                                new CodeExpressionStatement(new CodeMethodInvokeExpression(
                                    new CodeThisReferenceExpression(), method.Name, 
                                        method.Parameters
                                        .Cast<CodeParameterDeclarationExpression>()
                                        .Select(_=>new CodeVariableReferenceExpression(_.Name))
                                        .Cast<CodeExpression>()
                                        .ToArray())),
                                /*  return; */
                                new CodeMethodReturnStatement(),
                                /* } */
                            },
                            catchClauses: codeCatchClauses.ToArray()
                            ),
                        new CodeConditionStatement(
                            /* if ( (i+1) >= retryCount) */
                            new CodeBinaryOperatorExpression(
                                new CodeBinaryOperatorExpression(new CodeVariableReferenceExpression("i"), CodeBinaryOperatorType.Add, new CodePrimitiveExpression(1)),
                                CodeBinaryOperatorType.LessThanOrEqual,
                                new CodePrimitiveExpression(retryCount)
                                ),
                            /* { */
                            /*  testRunner.OnScenarioEnd();*/
                            new CodeExpressionStatement(
                                new CodeMethodInvokeExpression(
                                    GetTestRunnerExpression(),
                                    "OnScenarioEnd"
                                    )))
                            /* } */
                    })
                );
            testMethod.Statements.Add(new CodeConditionStatement(
                condition: new CodeBinaryOperatorExpression(
                    new CodeVariableReferenceExpression("lastException"),
                    CodeBinaryOperatorType.IdentityInequality,
                    new CodePrimitiveExpression(null)),
                trueStatements: new CodeThrowExceptionStatement(new CodeVariableReferenceExpression("lastException"))));

            return method;
        }

        private void GenerateTestBody(TestClassGenerationContext generationContext, Scenario scenario, CodeMemberMethod testMethod, CodeExpression additionalTagsExpression = null, ParameterSubstitution paramToIdentifier = null)
        {
            //call test setup
            //ScenarioInfo scenarioInfo = new ScenarioInfo("xxxx", tags...);
            CodeExpression tagsExpression;
            if (additionalTagsExpression == null)
                tagsExpression = GetStringArrayExpression(scenario.Tags);
            else if (scenario.Tags == null)
                tagsExpression = additionalTagsExpression;
            else
            {
                // merge tags list
                // var tags = tags1
                // if (tags2 != null)
                //   tags = Enumerable.ToArray(Enumerable.Concat(tags1, tags1));
                testMethod.Statements.Add(
                    new CodeVariableDeclarationStatement(typeof(string[]), "__tags", GetStringArrayExpression(scenario.Tags)));
                tagsExpression = new CodeVariableReferenceExpression("__tags");
                testMethod.Statements.Add(
                    new CodeConditionStatement(
                        new CodeBinaryOperatorExpression(
                            additionalTagsExpression,
                            CodeBinaryOperatorType.IdentityInequality,
                            new CodePrimitiveExpression(null)),
                        new CodeAssignStatement(
                            tagsExpression,
                            new CodeMethodInvokeExpression(
                                new CodeTypeReferenceExpression(typeof(Enumerable)),
                                "ToArray",
                                new CodeMethodInvokeExpression(
                                    new CodeTypeReferenceExpression(typeof(Enumerable)),
                                    "Concat",
                                    tagsExpression,
                                    additionalTagsExpression)))));
            }
            testMethod.Statements.Add(
                new CodeVariableDeclarationStatement(typeof(ScenarioInfo), "scenarioInfo",
                    new CodeObjectCreateExpression(typeof(ScenarioInfo),
                        new CodePrimitiveExpression(scenario.Title),
                        tagsExpression)));

            AddLineDirective(testMethod.Statements, scenario);
            testMethod.Statements.Add(
                new CodeMethodInvokeExpression(
                    new CodeThisReferenceExpression(),
                    generationContext.ScenarioInitializeMethod.Name,
                    new CodeVariableReferenceExpression("scenarioInfo")));

            if (HasFeatureBackground(generationContext.Feature))
            {
                AddLineDirective(testMethod.Statements, generationContext.Feature.Background);
                testMethod.Statements.Add(
                    new CodeMethodInvokeExpression(
                        new CodeThisReferenceExpression(),
                        generationContext.FeatureBackgroundMethod.Name));
            }

            foreach (var scenarioStep in scenario.Steps)
            {
                GenerateStep(testMethod, scenarioStep, paramToIdentifier);
            }

            AddLineDirectiveHidden(testMethod.Statements);

            // call scenario cleanup
            testMethod.Statements.Add(
                new CodeMethodInvokeExpression(
                    new CodeThisReferenceExpression(),
                    generationContext.ScenarioCleanupMethod.Name));
        }

        private CodeMemberMethod CreateTestMethod(TestClassGenerationContext generationContext, Scenario scenario, Tags additionalTags)
        {
            CodeMemberMethod testMethod = CreateMethod(generationContext.TestClass);

            SetupTestMethod(generationContext, testMethod, scenario, additionalTags);

            return testMethod;
        }

        private void SetupTestMethod(TestClassGenerationContext generationContext, CodeMemberMethod testMethod, Scenario scenario, Tags additionalTags, bool rowTest = false)
        {
            testMethod.Attributes = MemberAttributes.Public;
            testMethod.Name = string.Format(TEST_NAME_FORMAT, scenario.Title.ToIdentifier());

            if (rowTest)
                testGeneratorProvider.SetRowTest(generationContext, testMethod, scenario.Title);
            else
                testGeneratorProvider.SetTestMethod(generationContext, testMethod, scenario.Title);

            List<string> scenarioCategories;
            decoratorRegistry.DecorateTestMethod(generationContext, testMethod, ConcatTags(scenario.Tags, additionalTags), out scenarioCategories);

            if (scenarioCategories.Any())
                testGeneratorProvider.SetTestMethodCategories(generationContext, testMethod, scenarioCategories);
        }

        private IEnumerable<Tag> ConcatTags(params Tags[] tagLists)
        {
            return tagLists.Where(tagList => tagList != null).SelectMany(tagList => tagList);
        }

        private CodeExpression GetSubstitutedString(string text, ParameterSubstitution paramToIdentifier)
        {
            if (text == null)
                return new CodeCastExpression(typeof(string), new CodePrimitiveExpression(null));
            if (paramToIdentifier == null)
                return new CodePrimitiveExpression(text);

            Regex paramRe = new Regex(@"\<(?<param>[^\>]+)\>");
            string formatText = text.Replace("{", "{{").Replace("}", "}}");
            List<string> arguments = new List<string>();

            formatText = paramRe.Replace(formatText, match =>
            {
                string param = match.Groups["param"].Value;
                string id;
                if (!paramToIdentifier.TryGetIdentifier(param, out id))
                    return match.Value;
                int argIndex = arguments.IndexOf(id);
                if (argIndex < 0)
                {
                    argIndex = arguments.Count;
                    arguments.Add(id);
                }
                return "{" + argIndex + "}";
            });

            if (arguments.Count == 0)
                return new CodePrimitiveExpression(text);

            List<CodeExpression> formatArguments = new List<CodeExpression>();
            formatArguments.Add(new CodePrimitiveExpression(formatText));
            formatArguments.AddRange(arguments.Select(id => new CodeVariableReferenceExpression(id)).Cast<CodeExpression>());

            return new CodeMethodInvokeExpression(
                new CodeTypeReferenceExpression(typeof(string)),
                "Format",
                formatArguments.ToArray());
        }

        private void GenerateStep(CodeMemberMethod testMethod, ScenarioStep scenarioStep, ParameterSubstitution paramToIdentifier)
        {
            var testRunnerField = GetTestRunnerExpression();

            //testRunner.Given("something");
            List<CodeExpression> arguments = new List<CodeExpression>();
            arguments.Add(
                GetSubstitutedString(scenarioStep.Text, paramToIdentifier));
            if (scenarioStep.MultiLineTextArgument != null || scenarioStep.TableArg != null)
                AddLineDirectiveHidden(testMethod.Statements);
            arguments.Add(
                GetMultilineTextArgExpression(scenarioStep.MultiLineTextArgument, paramToIdentifier));
            arguments.Add(
                GetTableArgExpression(scenarioStep.TableArg, testMethod.Statements, paramToIdentifier));
            arguments.Add(new CodePrimitiveExpression(scenarioStep.Keyword));

            AddLineDirective(testMethod.Statements, scenarioStep);
            testMethod.Statements.Add(
                new CodeMethodInvokeExpression(
                    testRunnerField,
                    scenarioStep.GetType().Name,
                    arguments.ToArray()));
        }

        private int tableCounter = 0;
        private CodeExpression GetTableArgExpression(GherkinTable tableArg, CodeStatementCollection statements, ParameterSubstitution paramToIdentifier)
        {
            if (tableArg == null)
                return new CodeCastExpression(typeof(Table), new CodePrimitiveExpression(null));

            tableCounter++;

            //Table table0 = new Table(header...);
            var tableVar = new CodeVariableReferenceExpression("table" + tableCounter);
            statements.Add(
                new CodeVariableDeclarationStatement(typeof(Table), tableVar.VariableName,
                    new CodeObjectCreateExpression(
                        typeof(Table),
                        GetStringArrayExpression(tableArg.Header.Cells.Select(c => c.Value), paramToIdentifier))));

            foreach (var row in tableArg.Body)
            {
                //table0.AddRow(cells...);
                statements.Add(
                    new CodeMethodInvokeExpression(
                        tableVar,
                        "AddRow",
                        GetStringArrayExpression(row.Cells.Select(c => c.Value), paramToIdentifier)));
            }
            return tableVar;
        }

        private CodeExpression GetMultilineTextArgExpression(string multiLineTextArgument, ParameterSubstitution paramToIdentifier)
        {
            return GetSubstitutedString(multiLineTextArgument, paramToIdentifier);
        }

        #region Line pragma handling

        private void AddLinePragmaInitial(CodeTypeDeclaration testType, string sourceFile)
        {
            if (generatorConfiguration.AllowDebugGeneratedFiles)
                return;

            codeDomHelper.BindTypeToSourceFile(testType, Path.GetFileName(sourceFile));
        }

        private void AddLineDirectiveHidden(CodeStatementCollection statements)
        {
            if (generatorConfiguration.AllowDebugGeneratedFiles)
                return;

            codeDomHelper.AddDisableSourceLinePragmaStatement(statements);
        }

        private void AddLineDirective(CodeStatementCollection statements, Background background)
        {
            AddLineDirective(statements, background.FilePosition);
        }

        private void AddLineDirective(CodeStatementCollection statements, Scenario scenario)
        {
            AddLineDirective(statements, scenario.FilePosition);
        }

        private void AddLineDirective(CodeStatementCollection statements, ScenarioStep step)
        {
            AddLineDirective(statements, step.FilePosition);
        }

        private void AddLineDirective(CodeStatementCollection statements, FilePosition filePosition)
        {
            if (filePosition == null || generatorConfiguration.AllowDebugGeneratedFiles)
                return;

            codeDomHelper.AddSourceLinePragmaStatement(statements, filePosition.Line, filePosition.Column);
        }

        #endregion
    }


    public class RetryUnitTestFeatureGeneratorProvider : IFeatureGeneratorProvider
    {
        private readonly RetryUnitTestFeatureGenerator unitTestFeatureGenerator;

        public RetryUnitTestFeatureGeneratorProvider(RetryUnitTestFeatureGenerator unitTestFeatureGenerator)
        {
            this.unitTestFeatureGenerator = unitTestFeatureGenerator;
        }

        public int Priority
        {
            get { return PriorityValues.Normal; }
        }

        public bool CanGenerate(Feature feature)
        {
            return true;
        }

        public IFeatureGenerator CreateGenerator(Feature feature)
        {
            return unitTestFeatureGenerator;
        }
    }
}
