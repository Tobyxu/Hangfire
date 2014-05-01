﻿// This file is part of HangFire.
// Copyright © 2013-2014 Sergey Odinokov.
// 
// HangFire is free software: you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as 
// published by the Free Software Foundation, either version 3 
// of the License, or any later version.
// 
// HangFire is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Lesser General Public License for more details.
// 
// You should have received a copy of the GNU Lesser General Public 
// License along with HangFire. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using HangFire.Server;

namespace HangFire.Common
{
    /// <summary>
    /// Represents the information about background invocation of a method.
    /// </summary>
    public class Job : IJobPerformer
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="Job"/> class with
        /// a given method data and arguments.
        /// </summary>
        /// 
        /// <remarks>
        /// Each argument should be serialized into a string using the 
        /// <see cref="TypeConverter.ConvertToInvariantString(object)"/> method of
        /// a corresponding <see cref="TypeConverter"/> instance.
        /// </remarks>
        /// 
        /// <exception cref="ArgumentNullException"><paramref name="methodData"/> argument is null.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="arguments"/> argument is null.</exception>
        internal Job(Type type, MethodInfo method, string[] arguments)
        {
            if (type == null) throw new ArgumentNullException("type");
            if (method == null) throw new ArgumentNullException("method");
            if (arguments == null) throw new ArgumentNullException("arguments");

            Type = type;
            Method = method;
            Arguments = arguments;

            Validate();
        }

        public Type Type { get; private set; }
        public MethodInfo Method { get; private set; }

        /// <summary>
        /// Gets arguments array that will be passed to the method during its invocation.
        /// </summary>
        public string[] Arguments { get; private set; }

        public void Perform(JobActivator activator)
        {
            if (activator == null) throw new ArgumentNullException("activator");

            object instance = null;

            try
            {
                if (!Method.IsStatic)
                {
                    instance = Activate(activator);
                }

                var deserializedArguments = DeserializeArguments();
                InvokeMethod(instance, deserializedArguments);
            }
            finally
            {
                Dispose(instance);
            }
        }

        internal IEnumerable<JobFilterAttribute> GetTypeFilterAttributes(bool useCache)
        {
            return useCache
                ? ReflectedAttributeCache.GetTypeFilterAttributes(Type)
                : GetFilterAttributes(Type);
        }

        internal IEnumerable<JobFilterAttribute> GetMethodFilterAttributes(bool useCache)
        {
            return useCache
                ? ReflectedAttributeCache.GetMethodFilterAttributes(Method)
                : GetFilterAttributes(Method);
        }

        private static IEnumerable<JobFilterAttribute> GetFilterAttributes(MemberInfo memberInfo)
        {
            return memberInfo
                .GetCustomAttributes(typeof(JobFilterAttribute), inherit: true)
                .Cast<JobFilterAttribute>();
        }

        /// <summary>
        /// Creates a new instance of the <see cref="Job"/> class on a 
        /// basis of the given static method call expression.
        /// </summary>
        /// 
        /// <exception cref="ArgumentNullException"><paramref name="methodCall"/> argument is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="methodCall"/> expression body does not contain <see cref="MethodCallExpression"/>.</exception>
        public static Job FromExpression(Expression<Action> methodCall)
        {
            if (methodCall == null) throw new ArgumentNullException("methodCall");

            var callExpression = methodCall.Body as MethodCallExpression;
            if (callExpression == null)
            {
                throw new NotSupportedException("Expression body should be of type `MethodCallExpression`");
            }

            // TODO: user can call this method with instance method expression. We need to check for it.

            // Static methods can not be overridden in the derived classes, 
            // so we can take the method's declaring type.
            return new Job(
                callExpression.Method.DeclaringType, 
                callExpression.Method, 
                GetArguments(callExpression));
        }

        /// <summary>
        /// Creates a new instance of the <see cref="Job"/> class on a 
        /// basis of the given instance method call expression.
        /// </summary>
        /// 
        /// <exception cref="ArgumentNullException"><paramref name="methodCall"/> argument is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="methodCall"/> expression body does not contain <see cref="MethodCallExpression"/>.</exception>
        public static Job FromExpression<T>(Expression<Action<T>> methodCall)
        {
            if (methodCall == null) throw new ArgumentNullException("methodCall");

            var callExpression = methodCall.Body as MethodCallExpression;
            if (callExpression == null)
            {
                throw new NotSupportedException("Expression body should be of type `MethodCallExpression`");
            }

            return new Job(
                typeof(T), 
                callExpression.Method, 
                GetArguments(callExpression));
        }

        private void Validate()
        {
            if (Method.DeclaringType == null)
            {
                throw new NotSupportedException("Global methods are not supported. Use class methods instead.");
            }

            if (!Method.DeclaringType.IsAssignableFrom(Type))
            {
                throw new ArgumentException(String.Format(
                    "The type `{0}` must be derived from the `{1}` type.",
                    Method.DeclaringType,
                    Type));
            }

            if (!Method.IsPublic)
            {
                throw new NotSupportedException("Only public methods can be invoked in the background.");
            }

            var parameters = Method.GetParameters();

            if (parameters.Length != Arguments.Length)
            {
                throw new ArgumentException("Argument count must be equal to method parameter count.", "arguments");
            }

            foreach (var parameter in parameters)
            {
                // There is no guarantee that specified method will be invoked
                // in the same process. Therefore, output parameters and parameters
                // passed by reference are not supported.

                if (parameter.IsOut)
                {
                    throw new NotSupportedException(
                        "Output parameters are not supported: there is no guarantee that specified method will be invoked inside the same process.");
                }

                if (parameter.ParameterType.IsByRef)
                {
                    throw new NotSupportedException(
                        "Parameters, passed by reference, are not supported: there is no guarantee that specified method will be invoked inside the same process.");
                }
            }
        }

        private object Activate(JobActivator activator)
        {
            try
            {
                var instance = activator.ActivateJob(Type);

                if (instance == null)
                {
                    throw new InvalidOperationException(
                        String.Format("JobActivator returned NULL instance of the '{0}' type.", Type));
                }

                return instance;
            }
            catch (Exception ex)
            {
                throw new JobPerformanceException(
                    "An exception occured during job activation.",
                    ex);
            }
        }

        private object[] DeserializeArguments()
        {
            try
            {
                var parameters = Method.GetParameters();
                var result = new List<object>(Arguments.Length);

                for (var i = 0; i < parameters.Length; i++)
                {
                    var parameter = parameters[i];
                    var argument = Arguments[i];

                    object value;

                    if (parameter.ParameterType == typeof(object))
                    {
                        // Special case for handling object types, because string can not
                        // be converted to object type.
                        value = argument;
                    }
                    else
                    {
                        var converter = TypeDescriptor.GetConverter(parameter.ParameterType);
                        value = converter.ConvertFromInvariantString(argument);
                    }

                    result.Add(value);
                }

                return result.ToArray();
            }
            catch (Exception ex)
            {
                throw new JobPerformanceException(
                    "An exception occured during arguments deserialization.",
                    ex);
            }
        }

        private void InvokeMethod(object instance, object[] deserializedArguments)
        {
            try
            {
                Method.Invoke(instance, deserializedArguments);
            }
            catch (TargetInvocationException ex)
            {
                throw new JobPerformanceException(
                    "An exception occurred during performance of the job.",
                    ex.InnerException);
            }
        }

        private static void Dispose(object instance)
        {
            try
            {
                var disposable = instance as IDisposable;
                if (disposable != null)
                {
                    disposable.Dispose();
                }
            }
            catch (Exception ex)
            {
                throw new JobPerformanceException(
                    "Job has been performed, but an exception occured during disposal.",
                    ex);
            }
        }

        private static string[] GetArguments(MethodCallExpression callExpression)
        {
            Debug.Assert(callExpression != null);

            var arguments = callExpression.Arguments.Select(GetArgumentValue).ToArray();

            var serializedArguments = new List<string>(arguments.Length);
            foreach (var argument in arguments)
            {
                string value = null;

                if (argument != null)
                {
                    var converter = TypeDescriptor.GetConverter(argument.GetType());
                    value = converter.ConvertToInvariantString(argument);
                }

                // Logic, related to optional parameters and their default values, 
                // can be skipped, because it is impossible to omit them in 
                // lambda-expressions (leads to a compile-time error).

                serializedArguments.Add(value);
            }

            return serializedArguments.ToArray();
        }

        private static object GetArgumentValue(Expression expression)
        {
            Debug.Assert(expression != null);

            var constantExpression = expression as ConstantExpression;

            if (constantExpression != null)
            {
                return constantExpression.Value;
            }

            return CachedExpressionCompiler.Evaluate(expression);
        }
    }
}
