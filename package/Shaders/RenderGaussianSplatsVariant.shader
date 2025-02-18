// SPDX-License-Identifier: MIT
Shader "Gaussian Splatting/Render Splats Variant"
{   
	// Properties
 //    {
 //        _SplatOverColor("Main Texture", 2D) = "white" {}
	// }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }

        Pass
        {
            ZWrite Off
            Blend OneMinusDstAlpha One
            Cull Off
            
CGPROGRAM
//#pragma enable_d3d11_debug_symbols
#pragma vertex vert
#pragma fragment frag
#pragma require compute
#pragma use_dxc

#include "Lighting.cginc"
#include "GaussianSplatting.hlsl"

StructuredBuffer<uint> _OrderBuffer;
//Texture2D _SplatOverColor;
//_SplatOverColor ("Test Color", half4) = (1, 0.5, 0.5, 1)

struct v2f
{
    half4 col : COLOR0;
    float2 pos : TEXCOORD0;
    float4 vertex : SV_POSITION;
};

StructuredBuffer<SplatViewData> _SplatViewData;
ByteAddressBuffer _SplatSelectedBits;
uint _SplatBitsValid;

v2f vert (uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
    v2f o = (v2f)0;
    instID = _OrderBuffer[instID];
	SplatViewData view = _SplatViewData[instID];
	float4 centerClipPos = view.pos;
	bool behindCam = centerClipPos.w <= 0;
	if (behindCam)
	{
		o.vertex = asfloat(0x7fc00000); // NaN discards the primitive
	}
	else
	{
		o.col.r = f16tof32(view.color.x >> 16);
		o.col.g = f16tof32(view.color.x);
		o.col.b = f16tof32(view.color.y >> 16);
		o.col.a = f16tof32(view.color.y);

		uint idx = vtxID;
		float2 quadPos = float2(idx&1, (idx>>1)&1) * 2.0 - 1.0;
		quadPos *= 2;

		o.pos = quadPos;

		float2 deltaScreenPos = (quadPos.x * view.axis1 + quadPos.y * view.axis2) * 2 / _ScreenParams.xy;
		o.vertex = centerClipPos;
		o.vertex.xy += deltaScreenPos * centerClipPos.w;

		if(_SplatIslightened == 1)
		{

			half3 worldNormal = o.vertex;//UnityObjectToWorldNormal(o.vertex);
			// dot product between normal and light direction for
			// standard diffuse (Lambert) lighting
			half nl = max(0, dot(worldNormal, _WorldSpaceLightPos0.xyz));
			// factor in the light color
			o.col.rgb = o.col.rgb + nl * _LightColor0;
		}

		// is this splat selected?
		if (_SplatBitsValid)
		{
			uint wordIdx = instID / 32;
			uint bitIdx = instID & 31;
			uint selVal = _SplatSelectedBits.Load(wordIdx * 4);
			if (selVal & (1 << bitIdx))
			{
				o.col.a = -1;				
			}
		}
	}
    return o;
}

half4 frag (v2f i) : SV_Target
{
	float power = -dot(i.pos, i.pos);
	half alpha = exp(power);


	///////////////////////////////////////////////////////////////////////////////////////////////////////
	// New gaussian splat parameters
	///////////////////////////////////////////////////////////////////////////////////////////////////////
    // eclairage
	//i.col = i.col * _LightColor0;

	//half4 overCol = half4(100,0.5,10.0,2);
	
	// over color
	i.col = i.col * _SplatOverColor;

	// saturation
	float greyscale = dot(i.col.rgb, fixed3(.222, .707, .071));  // Convert to greyscale numbers with magic luminance numbers
	i.col.rgb = lerp(float3(greyscale, greyscale, greyscale), i.col.rgb, _SplatSaturation);//_Saturation

	// greyscale
	if(_SplatIsBlackAndWhite == 1)
	{
		float4 baseColour = i.col; 
		float greyscaleAverage = (baseColour.r + baseColour.g + baseColour.b)/3.0f; 
		i.col = float4(greyscaleAverage, greyscaleAverage, greyscaleAverage, baseColour.a);	
	}

	// draw splat outline
	if(_SplatIsOutlined == 1)
	{
		if (alpha > 7.0/255.0)
		{
			if (alpha < 10.0/255.0)
			{
				alpha = 1;
				//i.col.rgb = selectedColor;
			}
		}
	}

	///////////////////////////////////////////////////////////////////////////////////////////////////////

	if (i.col.a >= 0)
	{
		alpha = saturate(alpha * i.col.a);
	}
	else
	{
		// "selected" splat: magenta outline, increase opacity, magenta tint
		half3 selectedColor = half3(1,0,1);
		if (alpha > 7.0/255.0)
		{
			if (alpha < 10.0/255.0)
			{
				alpha = 1;
				i.col.rgb = selectedColor;
			}
			alpha = saturate(alpha + 0.3);
		}
		i.col.rgb = lerp(i.col.rgb, selectedColor, 0.5);
	}
	
    if (alpha < 1.0/255.0)
        discard;

    half4 res = half4(i.col.rgb * alpha, alpha);
    return res;
}
ENDCG
        }
    }
}
