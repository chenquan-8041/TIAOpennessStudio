using System;
using System.ComponentModel;
using System.Globalization;

namespace TIAOpennessStudio.Services.Enums
{
	/// <summary>TypeConverter that renders enum values using their <see cref="DescriptionAttribute"/> text.</summary>
	public class EnumToStringUsingDescription : TypeConverter
	{
		public override bool CanConvertFrom(ITypeDescriptorContext context, Type sourceType)
		{
			return sourceType == typeof(Enum);
		}

		public override bool CanConvertTo(ITypeDescriptorContext context, Type destinationType)
		{
			return destinationType == typeof(string);
		}

		public override object ConvertTo(ITypeDescriptorContext context, CultureInfo culture, object value, Type destinationType)
		{
			if (!(destinationType == typeof(string)))
			{
				throw new ArgumentException("Can only convert to string.", "destinationType");
			}
			if (!(value.GetType().BaseType == typeof(Enum)))
			{
				throw new ArgumentException("Can only convert an instance of enum.", "value");
			}
			string text = value.ToString();
			object[] customAttributes = value.GetType().GetField(text).GetCustomAttributes(typeof(DescriptionAttribute), false);
			if (customAttributes.Length == 0)
			{
				return text;
			}
			return ((DescriptionAttribute)customAttributes[0]).Description;
		}
	}
}