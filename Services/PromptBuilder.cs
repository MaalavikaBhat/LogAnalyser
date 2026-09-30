namespace LogAnalyser.Services
{
    public class PromptBuilder
    {
        public string BuildPrompt(List<string> logs)
        {
            var timeline = string.Join("\n- ", logs);

            return $@"
                Based on the timeline below, analyze what happened and respond with ONLY a JSON object in this exact format:
                {{
                    ""summary"": {{
                        ""whatSucceeded"": [""list of successful operations""],
                        ""whatFailed"": [""list of failed operations""]
                    }},
                    ""rootCause"": ""explanation of the root cause"",
                    ""signature"": {{
                        ""exceptionType"": ""stable exception type, or unknown"",
                        ""component"": ""failing service, dependency, or component, or unknown"",
                        ""normalizedMessage"": ""stable error text with IDs, timestamps, and variable values removed"",
                        ""errorCode"": ""stable HTTP, database, or application error code, or null""
                    }}
                }}

                The signature is used to find earlier occurrences. Do not include correlation IDs, operation IDs, timestamps,
                request IDs, user data, or other incident-specific values in normalizedMessage.

                Timeline:
                - {timeline}
            ";
        }
    }
}
