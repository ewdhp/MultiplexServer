using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

public class Reflection
{
  private Dictionary<string, object> _availableClasses;

  public Reflection(Dictionary<string, object> availableClasses)
  {
    _availableClasses = availableClasses;
  }

  public void Exec(string commandStr)
  {
    // Split command string into parts
    var parts = commandStr.Split(' ');
    if (parts.Length < 2)
    {
      Console.WriteLine("Invalid command format. Use: className functionName arg1 arg2 ...");
      return;
    }

    var className = parts[0];
    var functionName = parts[1];
    var args = parts.Skip(2).ToArray();

    // Check if class exists in dictionary
    if (!_availableClasses.ContainsKey(className))
    {
      Console.WriteLine($"Class '{className}' not found.");
      return;
    }

    // Get the class instance and type
    var instance = _availableClasses[className];
    var type = instance.GetType();

    // Find the method in the class
    var method = type.GetMethod(functionName);
    if (method == null)
    {
      Console.WriteLine($"Method '{functionName}' not found in class '{className}'.");
      return;
    }

    // Validate parameter count
    var parameters = method.GetParameters();
    if (parameters.Length != args.Length)
    {
      Console.WriteLine($"Parameter count mismatch. Expected {parameters.Length}, but got {args.Length}.");
      return;
    }

    // Convert arguments to appropriate types and invoke the method
    try
    {
      var convertedArgs = ConvertArguments(args, parameters);
      var result = method.Invoke(instance, convertedArgs);
      Console.WriteLine("Result: " + result);
    }
    catch (Exception ex)
    {
      Console.WriteLine("Error executing command: " + ex.Message);
    }
  }

  private object[] ConvertArguments(string[] args, ParameterInfo[] parameters)
  {
    var convertedArgs = new object[args.Length];
    for (int i = 0; i < args.Length; i++)
    {
      convertedArgs[i] = Convert.ChangeType(args[i], parameters[i].ParameterType);
    }
    return convertedArgs;
  }
}