using FluentAssertions;
using Milvaion.Application.Dtos.ActivityLogDtos;
using System.Reflection;
using Xunit.Abstractions;

namespace Milvaion.UnitTests.ComponentTests.DtoTests;

[Trait("DTO Unit Tests", "Comprehensive reflection-based DTO getter/setter tests.")]
public class ComprehensiveReflectionDtoTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void AllRequestedDtos_ShouldAllowSettingSimpleProperties()
    {
        Assembly assembly = typeof(ActivityLogListDto).Assembly; // anchor to application assembly

        // discover all DTO types under the Milvaion.Application.Dtos namespace
        Type[] dtoTypes = [.. assembly.GetTypes()
                                  .Where(t => t.Namespace != null &&
                                              (t.Namespace == "Milvaion.Application.Dtos" ||
                                               t.Namespace.StartsWith("Milvaion.Application.Dtos.")))];

        var failures = new List<string>();
        var successes = new List<string>();

        foreach (Type type in dtoTypes)
        {
            string name = type.Name;

            // skip generic type definitions, abstract types and interfaces
            if (type.IsGenericTypeDefinition || type.IsAbstract || type.IsInterface)
            {
                continue;
            }

            // try to create an instance using public or non-public constructors
            object instance = TryCreateInstance(type);
            if (instance == null)
            {
                // cannot instantiate - skip this DTO
                continue;
            }

            List<PropertyInfo> props = [.. type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite)];
            bool typeHadFailure = false;
            foreach (PropertyInfo p in props)
            {
                try
                {
                    object sample = CreateSampleValue(p.PropertyType);
                    if (sample == null)
                    {
                        continue; // skip complex
                    }

                    p.SetValue(instance, sample);
                    object read = p.GetValue(instance);

                    if (p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(List<>))
                    {
                        if (read == null)
                        {
                            failures.Add($"{name}.{p.Name} was null after set");
                            typeHadFailure = true;
                        }
                    }
                    else if (p.PropertyType.IsValueType || p.PropertyType == typeof(string) || p.PropertyType.IsEnum)
                    {
                        if (!object.Equals(read, sample))
                        {
                            failures.Add($"{name}.{p.Name} value mismatch (set {sample} read {read})");
                            typeHadFailure = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"{name}.{p.Name} exception: {ex.GetType().Name}");
                    typeHadFailure = true;
                }
            }

            if (!typeHadFailure)
                successes.Add(name);
        }

        // Print successful DTO types to test output for inspection
        if (successes.Count != 0)
        {
            _output.WriteLine($"ComprehensiveReflectionDtoTests - Successful DTOs({successes.Count}):");
            foreach (var s in successes.OrderBy(x => x))
                _output.WriteLine(s);
        }

        // Print successful DTO types to test output for inspection
        if (failures.Count != 0)
        {
            _output.WriteLine($"ComprehensiveReflectionDtoTests - Failed DTOs({failures.Count}):");

            foreach (var f in failures.OrderBy(x => x))
                _output.WriteLine(f);
        }

        failures.Should().BeEmpty("All DTOs should allow basic property set/get for primitive-like members");
    }

    private static object TryCreateInstance(Type type)
    {
        try
        {
            // try parameterless public
            return Activator.CreateInstance(type);
        }
        catch
        {
            // try constructors with parameters (public or non-public)
            ConstructorInfo[] ctors = [.. type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).OrderBy(c => c.GetParameters().Length)];
            foreach (ConstructorInfo ctor in ctors)
            {
                ParameterInfo[] pars = ctor.GetParameters();
                object[] args = new object[pars.Length];
                bool ok = true;
                for (int i = 0; i < pars.Length; i++)
                {
                    object sample = CreateSampleValue(pars[i].ParameterType);
                    if (sample == null)
                    {
                        ok = false;
                        break;
                    }

                    args[i] = sample;
                }

                if (!ok)
                    continue;
                try
                {
                    return ctor.Invoke(args);
                }
                catch
                {
                    continue;
                }
            }
        }

        return null;
    }

    private static object CreateSampleValue(Type t)
    {
        Type underlying = Nullable.GetUnderlyingType(t) ?? t;

        if (underlying == typeof(string))
            return "s";

        if (underlying == typeof(int))
            return 1;

        if (underlying == typeof(long))
            return 1L;

        if (underlying == typeof(bool))
            return true;

        if (underlying == typeof(double))
            return 1.1;

        if (underlying == typeof(decimal))
            return 1m;

        if (underlying == typeof(Guid))
            return Guid.NewGuid();

        if (underlying == typeof(DateTime))
            return DateTime.UtcNow;

        if (underlying.IsEnum)
            return Enum.GetValues(underlying).GetValue(0);

        if (underlying.IsGenericType && underlying.GetGenericTypeDefinition() == typeof(List<>))
        {
            Type elem = underlying.GetGenericArguments()[0];
            Type listType = typeof(List<>).MakeGenericType(elem);
            return Activator.CreateInstance(listType);
        }

        // do not attempt to instantiate complex DTO classes here - skip
        return null;
    }
}
