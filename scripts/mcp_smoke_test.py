import asyncio
import json
import os
from pathlib import Path

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client


def first_text(result):
    for item in result.content:
        if hasattr(item, "text"):
            return item.text
    return ""


async def main():
    repo_root = Path(__file__).resolve().parents[1]
    server_exe = repo_root / "src" / "TeklaMcp.Server" / "bin" / "Release" / "net48" / "TeklaMcp.Server.exe"
    if not server_exe.exists():
        raise FileNotFoundError("Server exe not found: " + str(server_exe))

    params = StdioServerParameters(
        command=str(server_exe),
        args=[],
        cwd=str(repo_root),
        env=os.environ.copy(),
    )

    async with stdio_client(params) as (read, write):
        async with ClientSession(read, write) as session:
            await session.initialize()

            tools = await session.list_tools()
            print("tools:")
            for tool in tools.tools:
                print(" -", tool.name)

            conn = await session.call_tool("tekla_get_connection_info", {})
            print("\ntekla_get_connection_info:")
            print(first_text(conn))

            summary = await session.call_tool("tekla_get_model_summary", {})
            print("\ntekla_get_model_summary:")
            summary_text = first_text(summary)
            print(summary_text)

            summary_data = json.loads(summary_text) if summary_text else {}
            top_material = ""
            count_by_material = summary_data.get("countByMaterial", {})
            if isinstance(count_by_material, dict) and count_by_material:
                top_material = max(count_by_material.items(), key=lambda kv: kv[1])[0]
                if top_material == "(none)":
                    real_materials = [(k, v) for k, v in count_by_material.items() if k != "(none)"]
                    if real_materials:
                        top_material = max(real_materials, key=lambda kv: kv[1])[0]

            listed = await session.call_tool("tekla_list_objects", {"limit": 5})
            print("\ntekla_list_objects(limit=5):")
            listed_text = first_text(listed)
            print(listed_text)

            list_data = json.loads(listed_text) if listed_text else []
            sample_guid = ""
            if isinstance(list_data, list) and list_data:
                first_obj = list_data[0]
                if isinstance(first_obj, dict):
                    sample_guid = first_obj.get("guid", "")

            if top_material:
                found = await session.call_tool("tekla_find_objects", {"material": top_material, "limit": 5})
                print(f"\ntekla_find_objects(material={top_material}, limit=5):")
                found_text = first_text(found)
                print(found_text)
                found_data = json.loads(found_text) if found_text else []
                if isinstance(found_data, list) and found_data:
                    first_found = found_data[0]
                    if isinstance(first_found, dict):
                        sample_guid = first_found.get("guid", "")

            if sample_guid:
                by_guid = await session.call_tool("tekla_get_object_by_guid", {"guid": sample_guid})
                print(f"\ntekla_get_object_by_guid(guid={sample_guid}):")
                print(first_text(by_guid))

            selected = await session.call_tool("tekla_get_selected_objects", {})
            print("\ntekla_get_selected_objects:")
            print(first_text(selected))

            by_material = await session.call_tool("tekla_analyze_by_material", {})
            print("\ntekla_analyze_by_material:")
            print(first_text(by_material))

            count_beams = await session.call_tool("tekla_count_objects", {"type": "Beam"})
            print("\ntekla_count_objects(type=Beam):")
            print(first_text(count_beams))

            sum_columns = await session.call_tool("tekla_sum_weight", {"type": "Beam", "nameContains": "Колонна"})
            print("\ntekla_sum_weight(type=Beam, nameContains=Колонна):")
            print(first_text(sum_columns))

            by_profile = await session.call_tool("tekla_group_weight_by", {"groupBy": "profile", "type": "Beam", "limit": 10})
            print("\ntekla_group_weight_by(groupBy=profile, type=Beam):")
            print(first_text(by_profile))

            distinct_material = await session.call_tool("tekla_list_distinct_values", {"field": "material", "limit": 10})
            print("\ntekla_list_distinct_values(field=material):")
            print(first_text(distinct_material))

            select_class_20 = await session.call_tool("tekla_select_objects", {"class": "20", "limit": 200})
            print("\ntekla_select_objects(class=20, limit=200):")
            print(first_text(select_class_20))

            if sample_guid:
                read_udas = await session.call_tool(
                    "tekla_get_object_udas",
                    {"guid": sample_guid, "udaNames": "USER_FIELD_1;MCP_TEST_TAG"},
                )
                print(f"\ntekla_get_object_udas(guid={sample_guid}):")
                print(first_text(read_udas))

                preview_set_udas = await session.call_tool(
                    "tekla_set_object_udas",
                    {"guid": sample_guid, "updates": "MCP_TEST_TAG=from_mcp_preview", "apply": False},
                )
                print(f"\ntekla_set_object_udas PREVIEW(guid={sample_guid}):")
                print(first_text(preview_set_udas))
                if os.environ.get("MCP_SMOKE_APPLY") == "1":
                    apply_set_udas = await session.call_tool(
                        "tekla_set_object_udas",
                        {"guid": sample_guid, "updates": "MCP_TEST_TAG=from_mcp_apply", "apply": True},
                    )
                    print(f"\ntekla_set_object_udas APPLY(guid={sample_guid}):")
                    print(first_text(apply_set_udas))

                    read_udas_after_apply = await session.call_tool(
                        "tekla_get_object_udas",
                        {"guid": sample_guid, "udaNames": "MCP_TEST_TAG"},
                    )
                    print(f"\ntekla_get_object_udas AFTER APPLY(guid={sample_guid}):")
                    print(first_text(read_udas_after_apply))


if __name__ == "__main__":
    asyncio.run(main())
