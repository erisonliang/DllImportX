using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace System.Runtime.InteropServices
{
    public class DllImportXFactory
    {
        public const string InvalidTypeExceptionMessage = "";

        public static T Build<T>() where T : class => Build<T>(null);

        public static T Build<T>(DllImportXFilter filter) where T : class
        {
            var type = typeof(T);

            var concreteType = ImplementInterface(type, filter);
            return (T)Activator.CreateInstance(concreteType);
        }

        public static Type ImplementInterface(Type iface, DllImportXFilter filter)
        {
            var assemblyName = new AssemblyName("DllImportX");

            var assemblyBuilder = AppDomain.CurrentDomain.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
            var moduleBuilder = assemblyBuilder.DefineDynamicModule(assemblyName.Name);
            var typeBuilder = DeclareType(iface, moduleBuilder);
            var methods = GetMethods(iface, filter, out var otherMethods);

            DefineImportMethods(typeBuilder, methods);
            DefineNotImplementedMethods(typeBuilder, otherMethods);

            return typeBuilder.CreateType();
        }

        public static TypeBuilder DeclareType(Type iface, ModuleBuilder moduleBuilder)
        {
            ValidateInterface(iface);

            var typeBuilder = moduleBuilder.DefineType(iface.Name + " (Implementation)", TypeAttributes.Public);
            typeBuilder.AddInterfaceImplementation(iface);

            return typeBuilder;
        }

        private static void ValidateInterface(Type type)
        {
            if (!type.IsInterface)
                throw new InvalidOperationException(InvalidTypeExceptionMessage);
        }

        private static IEnumerable<DllImportXOptions> GetMethods(Type iface, DllImportXFilter filter, out IEnumerable<MethodInfo> others)
        {
            var methods = new { Imports = new List<DllImportXOptions>(), Others = new List<MethodInfo>() };

            foreach (var x in iface.GetMethods())
            {
                if (x.GetCustomAttributes(typeof(DllImportXAttribute), false).FirstOrDefault() == null)
                    methods.Others.Add(x);
                else
                    methods.Imports.Add(new DllImportXOptions(x));
            }

            if (filter != null)
            {
                foreach (var import in methods.Imports)
                    filter(import);
            }

            others = methods.Others;
            return methods.Imports;
        }

        private static void DefineImportMethods(TypeBuilder typeBuilder, IEnumerable<DllImportXOptions> imports)
        {
            foreach (var options in imports)
                ImplementMethod(typeBuilder, options);
        }

        private static void ImplementMethod(TypeBuilder typeBuilder, DllImportXOptions options)
        {
            var method = options.Method;
            var parameters = method.GetParameters().Select(x => x.ParameterType).ToArray();
            var callee = DefinePInvokeMethod(typeBuilder, options);
            var caller = typeBuilder.DefineMethod(method.Name, MethodAttributes.Virtual, method.ReturnType, parameters);
            var il = caller.GetILGenerator();

            for (int i = 1, c = parameters.Length; i <= c; i++)
            {
                il.Emit(OpCodes.Ldarg, i);
            }
            il.Emit(OpCodes.Call, callee);
            il.Emit(OpCodes.Ret);

            typeBuilder.DefineMethodOverride(caller, options.Method);
        }

        public static MethodInfo DefinePInvokeMethod(TypeBuilder typeBuilder, DllImportXOptions options)
        {
            var clrImportType = typeof(DllImportAttribute);
            var ctor = clrImportType.GetConstructor(new[] { typeof(string) });

            var fields = new[] {
                clrImportType.GetField("EntryPoint"),
                clrImportType.GetField("ExactSpelling"),
                clrImportType.GetField("PreserveSig"),
                clrImportType.GetField("SetLastError"),
                clrImportType.GetField("CallingConvention"),
                clrImportType.GetField("CharSet"),
                clrImportType.GetField("BestFitMapping"),
                clrImportType.GetField("ThrowOnUnmappableChar")
            };

            var fieldValues = new object[] {
                options.EntryPoint,
                options.ExactSpelling,
                options.PreserveSig,
                options.SetLastError,
                options.CallingConvention,
                options.CharSet,
                options.BestFitMapping,
                options.ThrowOnUnmappableChar
            };

            var clrImport = new CustomAttributeBuilder(ctor, new[] { options.DllName }, fields, fieldValues);
            var method = typeBuilder.DefineMethod(
                options.Method.Name,
                MethodAttributes.Private | MethodAttributes.Static,
                options.Method.ReturnType,
                options.Method.GetParameters().Select(x => x.ParameterType).ToArray()
            );

            method.SetCustomAttribute(clrImport);
            return method;
        }

        private static void DefineNotImplementedMethods(TypeBuilder typeBuilder, IEnumerable<MethodInfo> otherMethods)
        {
            var clrExceptionType = typeof(NotImplementedException);
            var ctor = clrExceptionType.GetConstructor(Type.EmptyTypes);

            foreach (var method in otherMethods)
            {
                var caller = typeBuilder
                    .DefineMethod(
                        method.Name,
                        MethodAttributes.Virtual,
                        method.ReturnType,
                        method.GetParameters().Select(x => x.ParameterType).ToArray()
                    );

                var il = caller.GetILGenerator();
                il.Emit(OpCodes.Newobj, ctor);
                il.Emit(OpCodes.Throw);

                typeBuilder.DefineMethodOverride(caller, method);
            }
        }
    }
}
